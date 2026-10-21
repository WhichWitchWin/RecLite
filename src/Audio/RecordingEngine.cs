using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RecLite.Audio
{
    /// <summary>
    /// 录音引擎。
    ///
    /// 工作方式：
    ///   1. StartDevice() 打开设备后，采集流一直运行，数据写入预录环形缓存 + 波形分析器
    ///   2. StartRecording() 时，先把环形缓存里已有的 N 分钟倒进文件，然后后续数据直接写文件
    ///   3. StopRecording() 结束写盘
    ///
    /// 因为从头到尾是同一股采集流，预录段与正式段之间天然无缝。
    /// </summary>
    public class RecordingEngine : IDisposable
    {
        private readonly object _sync = new object();

        private IWaveIn _capture;
        private PrerollBuffer _preroll;
        private readonly WaveformAnalyzer _waveform = new WaveformAnalyzer();
        private IAudioSink _sink;

        private float[] _floatBuf = new float[0];
        private byte[] _rawBuf = new byte[0];
        private int _channels = 2;
        private int _sampleRate = 48000;
        private int _bitsPerSample = 32;
        private bool _isFloat;
        private bool _isRecording;
        private volatile bool _disposed;

        /// <summary>发生错误（设备异常、写盘失败等）</summary>
        public event Action<string> Error;
        /// <summary>采集数据到达（用于界面刷新电平）</summary>
        public event Action DataArrived;

        public WaveformAnalyzer Waveform => _waveform;
        public bool IsDeviceOpen => _capture != null;
        public bool IsRecording { get { lock (_sync) { return _isRecording; } } }
        public int SampleRate => _sampleRate;
        public int Channels => _channels;
        public int BitsPerSample => _bitsPerSample;

        /// <summary>
        /// 开始录制那一刻，预录缓存里到底有多少秒。
        ///
        /// 为什么要存这个：StartRecording 会把缓存 DrainAll 之后置空，
        /// 从此 PrerollSeconds 只能读到 0，全程序再没人知道「这次预录是 3 秒」。
        /// 时间轴靠这个快照才知道绿条该画多长、黄条从哪开始接。
        /// </summary>
        private double _prerollSnapshot;

        /// <summary>
        /// 当前预录时长（秒）。
        /// 录制中返回开始那一刻冻结的快照 —— 否则缓存已倒空，绿条会瞬间消失。
        /// </summary>
        public double PrerollSeconds
        {
            get
            {
                lock (_sync)
                {
                    if (_isRecording) return _prerollSnapshot;
                    return _preroll == null ? 0 : _preroll.BufferedSeconds(_sampleRate);
                }
            }
        }

        /// <summary>
        /// 当前采样率下，预录缓存的秒数上限 —— **全程序只认这一个入口**。
        ///
        /// 采样率只在本类的 _sampleRate 存一份（打开设备时从设备格式读到），
        /// 上限由它现算，不缓存任何副本 —— 换设备后读这个属性自然就是新值。
        /// 界面要夹取输入、要写悬浮说明，都从这里取，不要各自去算。
        /// </summary>
        public int MaxPrerollSeconds
        {
            get
            {
                lock (_sync)
                    return PrerollBuffer.MaxSecondsFor(_sampleRate);
            }
        }

        /// <summary>当前正在写入的文件路径（未录制时为 null）</summary>
        public string CurrentFilePath { get; private set; }

        /// <summary>录制增益（软件内部，不改系统音量）。1.0 = 原样，2.0 = +6dB</summary>
        public float InputGain { get; set; } = 1.0f;

        /// <summary>是否处于暂停状态（暂停时既不写文件也不缓存）</summary>
        public bool IsPaused { get; private set; }

        /// <summary>暂停录制（停止写入，数据丢弃）</summary>
        public void PauseRecording()
        {
            lock (_sync) { if (_isRecording) IsPaused = true; }
        }

        /// <summary>从暂停恢复录制</summary>
        public void ResumeRecording()
        {
            lock (_sync) { IsPaused = false; }
        }

        // ==================== 设备 ====================

        /// <summary>
        /// 麦克风采集缓冲候选（毫秒），从低到高逐个尝试。
        /// 保守起见最低档取 30ms：20ms 在专业声卡上没问题，但笔记本板载声卡 / 蓝牙耳机 /
        /// 廉价 USB 声卡上可能打开成功却出现咔哒声、断音，那是自动回退兜不住的。
        /// </summary>
        private static readonly int[] MicBufferCandidatesMs = { 30, 50 };

        /// <summary>当前实际使用的采集缓冲（毫秒）</summary>
        private int _usedBufferMs = 50;

        /// <summary>
        /// 已经证明「会出问题」的缓冲下限。运行中一旦遇到设备中断，就把它抬到 50ms，
        /// 下次打开设备时直接跳过更小的值 —— 保证同一台机器上不会反复踩同一个坑。
        /// </summary>
        private int _bufferFloorMs;

        /// <summary>打开一个录音源（采集设备或播放设备回环），并开始持续采集</summary>
        /// <param name="prerollSeconds">预录缓存秒数；0 = 关闭（不缓存任何数据）</param>
        public void StartDevice(MMDevice device, DeviceKind kind, int prerollSeconds)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            lock (_sync)
            {
                CloseDeviceInternal();

                int seconds = Math.Max(0, prerollSeconds);

                // WASAPI 默认缓冲区 100ms，一秒只回调约 10 次，波形会「一跳一跳」。
                // 这里优先压到 30ms，设备不接受再回退到 50ms。
                // 回环采集（录电脑声）在 NAudio 里只有 2 参数构造，沿用系统默认（约 100ms）。
                int[] candidates = kind == DeviceKind.Loopback
                    ? new[] { 100 }
                    : MicBufferCandidatesMs;

                Exception lastError = null;
                foreach (int bufferMs in candidates)
                {
                    if (bufferMs < _bufferFloorMs) continue;   // 之前证明过更小的档位不可用，直接跳过

                    IWaveIn capture = null;
                    try
                    {
                        capture = kind == DeviceKind.Loopback
                            ? (IWaveIn)new WasapiLoopbackCapture(device)
                            : new WasapiCapture(device, true, bufferMs);

                        capture.DataAvailable += OnDataAvailable;
                        capture.RecordingStopped += OnRecordingStopped;
                        _capture = capture;

                        // 格式状态必须在 StartRecording 之前就绪，
                        // 否则第一批音频回调会拿到上一台设备的声道 / 采样率。
                        var fmt = capture.WaveFormat;
                        _sampleRate = fmt.SampleRate;
                        _channels = Math.Max(1, fmt.Channels);
                        _bitsPerSample = fmt.BitsPerSample;
                        _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;

                        _waveform.Reset(_sampleRate, _channels);

                        _preroll = seconds > 0
                            ? new PrerollBuffer(seconds, _sampleRate, _channels, fmt.BlockAlign)
                            : null;   // 关闭预录：不分配、不缓存

                        capture.StartRecording();   // 真正启动：设备不接受这个缓冲时在这里抛

                        _usedBufferMs = bufferMs;
                        Core.Log.Write(string.Format("[采集] 已打开：缓冲 {0} ms，{1} 声道 / {2} Hz / {3} bit",
                            bufferMs, _channels, _sampleRate, _bitsPerSample));
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        Core.Log.Write(string.Format("[采集] 缓冲 {0} ms 打开失败，尝试回落：{1}",
                            bufferMs, ex.Message));
                        if (capture != null)
                        {
                            try { capture.DataAvailable -= OnDataAvailable; } catch { }
                            try { capture.RecordingStopped -= OnRecordingStopped; } catch { }
                            try { capture.StopRecording(); } catch { }
                            try { capture.Dispose(); } catch { }
                        }
                        _capture = null;
                        _preroll = null;
                    }
                }

                throw new InvalidOperationException("无法打开采集设备：" +
                    (lastError != null ? lastError.Message : "未知错误"));
            }
        }

        /// <summary>
        /// 运行中调整预录时长（0 = 关闭）。
        /// 已缓存的数据保留：调大继续往后填，调小丢掉最老的那截。
        /// 录音进行中调用会被忽略，避免丢数据。
        /// </summary>
        public void SetPrerollSeconds(int seconds)
        {
            lock (_sync)
            {
                if (_capture == null || _isRecording) return;
                int sec = PrerollBuffer.ClampSeconds(seconds, _sampleRate);

                if (sec <= 0)
                {
                    _preroll = null;   // 关闭：直接丢掉已缓存的数据
                    return;
                }

                if (_preroll == null)
                {
                    int ba = 1;
                    try { ba = Math.Max(1, _capture.WaveFormat.BlockAlign); } catch { }
                    _preroll = new PrerollBuffer(sec, _sampleRate, _channels, ba);
                }
                else
                {
                    // 已存在：只改容量，数据留着（变小丢最老的，变大接着往后填）
                    _preroll.SetSeconds(sec, _sampleRate);
                }
            }
        }

        /// <summary>清空预录缓存（「重置预录」：丢掉已缓存那截，从现在重新开始填）</summary>
        public void ClearPreroll()
        {
            lock (_sync) { _preroll?.Clear(); }
        }

        /// <summary>
        /// 把预录缓存另存成 WAV（「保存预录」）。
        /// 与正式录音共用同一套增益，存出来的电平跟听到的一致。
        ///
        /// 【锁的边界很关键】锁内只做纯内存操作（取快照 + 转换 + 施加增益，几十毫秒）；
        /// 「写磁盘」必须挪到锁外 —— 几十 MB 落盘要几百毫秒，占着音频锁会把采集回调堵住，
        /// 那段时间的音频就进不了预录缓存，预录会凭空缺一段。
        /// </summary>
        public bool SavePrerollToWav(string path, int bits, out string error)
        {
            error = null;

            float[] data;
            int frames;
            int sampleRate, channels;
            int b = bits == 16 ? 16 : bits == 24 ? 24 : 32;

            lock (_sync)
            {
                byte[] raw = _preroll == null ? null : _preroll.PeekAll();
                if (raw == null || raw.Length == 0)
                {
                    error = "预录缓存里还没有数据";
                    return false;
                }

                sampleRate = _sampleRate;
                channels = _channels;

                var floats = RawToFloat(raw, raw.Length, out frames);
                if (frames <= 0)
                {
                    error = "预录数据无法解析";
                    return false;
                }

                // 拷一份再施加增益：floats 指向复用的 _floatBuf，
                // 下一次 RawToFloat 会把它覆盖掉，不能就地改也不能留着引用。
                data = new float[frames * channels];
                Buffer.BlockCopy(floats, 0, data, 0, data.Length * 4);
                ApplyGain(data, frames);
            }

            // ===== 以下在锁外：磁盘写不能阻塞音频线程 =====
            try
            {
                using (var sink = new WavSink(path, sampleRate, channels, b))
                    sink.Write(data, frames);
                return true;
            }
            catch (Exception ex)
            {
                // 兜底：删掉可能留下的半截文件。
                // WavSink 是先创建文件、再往里写数据的，写到一半失败就会留下
                // 0 字节或只有文件头的 WAV，不清理的话会一直躺在录音目录里。
                try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch (Exception) { }
                error = ex.Message;
                return false;
            }
        }

        /// <summary>关闭设备</summary>
        public void StopDevice()
        {
            lock (_sync) { CloseDeviceInternal(); }
        }

        private void CloseDeviceInternal()
        {
            if (_isRecording) StopRecordingInternal();
            CloseCaptureOnly();
            _preroll = null;
            _prerollSnapshot = 0;
        }

        /// <summary>
        /// 只关闭采集设备，不动正在写入的文件、也不改录音状态。
        /// </summary>
        private void CloseCaptureOnly()
        {
            if (_capture == null) return;

            try
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.StopRecording();
                _capture.Dispose();
            }
            catch (Exception ex) { Core.Log.Error("CloseCaptureOnly", ex); }
            _capture = null;
        }

        // ==================== 录制 ====================

        /// <summary>
        /// 开始录制。会先把预录缓存里的内容写入文件开头。
        /// </summary>
        public void StartRecording(string path, string format, int bits, int bitrate, int targetSampleRate, bool includePreroll = true)
        {
            lock (_sync)
            {
                if (_capture == null) throw new InvalidOperationException("设备尚未打开");
                if (_isRecording) return;

                IAudioSink sink;
                if (string.Equals(format, "mp3", StringComparison.OrdinalIgnoreCase))
                {
                    // MP3 采用「先写 WAV 临时文件，停止后转码」策略，见 StopRecording
                    sink = new WavSink(path, _sampleRate, _channels, 16);
                }
                else
                {
                    int b = bits <= 0 ? 32 : bits;
                    sink = new WavSink(path, _sampleRate, _channels, b);
                }

                _sink = sink;
                CurrentFilePath = path;

                IsPaused = false;

                bool usePre = includePreroll && _preroll != null;

                // 先冻结「点击那一刻缓存里有多少秒」——DrainAll 之后缓存立刻变空，
                // 时间轴只能靠这个快照继续画绿条。
                _prerollSnapshot = usePre
                    ? _preroll.BufferedSeconds(_sampleRate)
                    : 0;

                if (usePre)
                {
                    // 把点击「录音」之前的那段接上去 —— 天然无缝
                    var pre = _preroll.DrainAll();
                    if (pre.Length > 0)
                    {
                        int preFrames;
                        var floats = RawToFloat(pre, pre.Length, out preFrames);
                        if (preFrames > 0)
                        {
                            ApplyGain(floats, preFrames);
                            _sink.Write(floats, preFrames);
                        }
                    }
                }
                else
                {
                    // 用户选择「不包含预录」：清空缓存，从零开始
                    _preroll?.Clear();
                }

                _isRecording = true;
            }
        }

        /// <summary>就地应用录制增益并做软限幅</summary>
        private void ApplyGain(float[] data, int frames)
        {
            float g = InputGain;
            if (Math.Abs(g - 1.0f) < 0.001f) return;
            int n = frames * _channels;

            // 这里不做限幅：增益后的值允许超过 ±1.0。
            // 原来把结果夹在 ±1.0，等于「拉高输入音量导致的过载」永远看不见，
            // 波形最多顶到 0 dB 线、进不了 +6 dB 区，32 bit 浮点能表示的超幅信息也被白白裁掉。
            // 真正写 16/24 bit 时由编码器按目标格式截断，那才是该削波的地方。
            for (int i = 0; i < n; i++)
                data[i] *= g;
        }

        /// <summary>停止录制，返回写入的帧数</summary>
        public long StopRecording()
        {
            lock (_sync) { return StopRecordingInternal(); }
        }

        private long StopRecordingInternal()
        {
            if (!_isRecording) return 0;
            _isRecording = false;
            long frames = 0;
            if (_sink != null)
            {
                frames = _sink.FramesWritten;
                try { _sink.Close(); } catch (Exception) { }
                try { _sink.Dispose(); } catch (Exception) { }
                _sink = null;
            }
            // 清空预录，避免下一段开头混入上一段内容
            if (_preroll != null) _preroll.Clear();
            _prerollSnapshot = 0;
            return frames;
        }

        // ==================== 采集回调 ====================

        /// <summary>
        /// 上次打「等锁超时」日志的时间戳。用来节流 ——
        /// 卡住时音频回调约 30ms 一次，不节流就是 33 条/秒的日志洪水，
        /// 几分钟就能把 8MB 轮转日志刷满，反而把现场冲掉。
        /// </summary>
        private long _lastLockTimeoutTicks;

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (_disposed || e.BytesRecorded <= 0) return;
            try
            {
                float[] floats;
                int frames;

                // 等锁超过 1 秒就打一条 —— 这是「有别的线程长期占着 _sync」的铁证。
                // 切设备卡死时，如果 UI 线程在锁内等 StopRecording、音频线程在锁外等锁，
                // 这条日志会直接把它指出来。
                bool gotLock = false;
                System.Threading.Monitor.TryEnter(_sync, 1000, ref gotLock);
                if (!gotLock)
                {
                    // 等满 1 秒还拿不到锁 —— 说明有别的线程长期占着它。
                    // 最典型的就是 UI 线程在 CloseDeviceInternal 里等 StopRecording()，
                    // 而 StopRecording() 又要等本采集线程退出：这时候再硬等就是永久死锁。
                    //
                    // 所以这里直接丢弃这一帧：丢 30ms 音频，总好过整个程序卡死。
                    // 日志每 5 秒最多一条，避免刷爆日志。
                    long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (nowTicks - _lastLockTimeoutTicks > System.Diagnostics.Stopwatch.Frequency * 5)
                    {
                        _lastLockTimeoutTicks = nowTicks;
                        Core.Log.Write("[采集] 等 _sync 超过 1 秒，本帧丢弃（有别的线程长期占着这把锁）");
                    }
                    return;
                }

                try
                {
                        if (_capture == null) return;
                        floats = RawToFloat(e.Buffer, e.BytesRecorded, out frames);
                        if (frames <= 0) return;

                        // 软件增益必须先作用到 floats：
                        // 待机波形、预录、录制三条路径看到的都该是「调过输入音量之后」的信号。
                        // 原来只在录制分支里加，于是「待机调音量没反应、点录音才管用」。
                        // 预录缓存仍写原始字节（取用时 DrainAll 后再加增益），所以不会重复放大。
                        ApplyGain(floats, frames);

                        if (_isRecording)
                        {
                            if (!IsPaused)
                            {
                                try { _sink?.Write(floats, frames); }
                                catch (Exception ex) { RaiseError("写盘失败：" + ex.Message); }
                            }
                            // 暂停中：数据直接丢弃（既不写盘也不进缓存）
                        }
                        else
                        {
                            // 待机中：只进预录缓存
                            _preroll?.Write(e.Buffer, 0, e.BytesRecorded);
                        }

                        // 波形分析必须在这把锁内做！
                        // floats 来自 RawToFloat 复用的 _floatBuf，出了锁之后下一次音频回调
                        // 随时会把它覆盖掉 —— 原来放在锁外，等于拿一块正被改写的内存去算包络，
                        // 结果就是波形点变少、波形跳跃（实测只有 61 点/秒，应为 66.7）。
                        //
                        // 暂停时不推进波形：暂停期间数据既不写盘也不进缓存，
                        // 而秒数指示器和时间轴都是冻结的（用 _frozenElapsed）。
                        // 波形要是继续滚，画面上就会出现「文件里根本不存在的音频」。
                        if (!(_isRecording && IsPaused))
                            _waveform.Process(floats, frames);
                    }
                    finally
                    {
                        // 与上面的 Monitor.TryEnter 配对（lock 换成 Monitor 了）
                        System.Threading.Monitor.Exit(_sync);
                    }

                DataArrived?.Invoke();
            }
            catch (Exception ex)
            {
                RaiseError("采集异常：" + ex.Message);
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception == null) return;

            // 设备中断：很可能是缓冲压得太小、驱动扛不住。
            // 把下限抬到 50ms，下次打开设备就自动用更大的缓冲 —— 同一台机器不会反复踩同一个坑。
            if (_usedBufferMs > 0 && _usedBufferMs < 50)
            {
                _bufferFloorMs = 50;
                Core.Log.Write(string.Format("[采集] 设备中断（当前缓冲 {0} ms），下次自动改用 50 ms：{1}",
                    _usedBufferMs, e.Exception.Message));
            }

            // 状态机必须在这里复位。
            // 否则 IsDeviceOpen 一直是 true、界面永远显示「正在录」，
            // 而且 WAV 文件头不会写回 —— 用户看到的是一个「0 秒的录音文件」，
            // 只能自己再点一次「停止」才自愈，那之前录的内容也没了。
            //
            // 这个事件是采集线程在退出前发出的，此时不会再有 OnDataAvailable 来抢锁。
            try
            {
                lock (_sync) { if (_isRecording) StopRecordingInternal(); }
            }
            catch (Exception ex)
            {
                Core.Log.Write("[采集] 设备中断后收尾失败：" + ex.Message);
            }

            RaiseError("设备中断：" + e.Exception.Message);
        }

        // ==================== 格式转换 ====================

        /// <summary>原始采集字节 → 32bit 浮点交错数据；frames 输出真实帧数（缓冲区可能比有效数据长）</summary>
        private float[] RawToFloat(byte[] raw, int count, out int frames)
        {
            frames = 0;
            int ch = _channels;
            if (ch <= 0) return Array.Empty<float>();

            // 32bit 浮点：字节数 / 4 = 样本数；样本数 / 声道数 = 帧数
            if (_isFloat && _bitsPerSample == 32)
            {
                int samples = count / 4;
                frames = samples / ch;
                int n = frames * ch;
                if (n <= 0) return Array.Empty<float>();
                if (_floatBuf.Length < n) _floatBuf = new float[n];
                Buffer.BlockCopy(raw, 0, _floatBuf, 0, n * 4);
                return _floatBuf;
            }

            if (_bitsPerSample == 16)
            {
                int samples = count / 2;
                frames = samples / ch;
                int n = frames * ch;
                if (n <= 0) return Array.Empty<float>();
                if (_floatBuf.Length < n) _floatBuf = new float[n];
                for (int i = 0; i < n; i++)
                {
                    short v = (short)(raw[i * 2] | (raw[i * 2 + 1] << 8));
                    _floatBuf[i] = v / 32768f;
                }
                return _floatBuf;
            }

            if (_bitsPerSample == 24)
            {
                int samples = count / 3;
                frames = samples / ch;
                int n = frames * ch;
                if (n <= 0) return Array.Empty<float>();
                if (_floatBuf.Length < n) _floatBuf = new float[n];
                for (int i = 0; i < n; i++)
                {
                    int b0 = raw[i * 3], b1 = raw[i * 3 + 1], b2 = raw[i * 3 + 2];
                    int v = (b2 << 16) | (b1 << 8) | b0;
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    _floatBuf[i] = v / 8388608f;
                }
                return _floatBuf;
            }

            return Array.Empty<float>();
        }

        private void RaiseError(string msg)
        {
            try { Error?.Invoke(msg); } catch (Exception) { }
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_sync) { CloseDeviceInternal(); }
        }
    }
}
