using System;

namespace RecLite.Audio
{
    /// <summary>
    /// 预录环形缓存。
    ///
    /// 原理：采集流从程序启动就一直开着，数据不断写进这块固定大小的内存；
    /// 写满后最旧的数据被最新的覆盖 —— 相当于「行车记录仪」。
    /// 用户点「开始录音」时，把这块内存里已有的 N 分钟数据先倒进文件开头，
    /// 之后的数据继续追加 —— 因为从头到尾是同一股连续数据流，天然无缝，不需要任何拼接算法。
    ///
    /// 线程安全：采集线程写，界面/写盘线程读。
    /// </summary>
    public class PrerollBuffer
    {
        /// <summary>
        /// 给定采样率下的预录缓存时长上限（秒）。
        ///
        /// 【纯函数】不读全局状态，采样率由调用方传进来 ——
        /// 全程序的采样率只有一个权威来源：RecordingEngine.SampleRate。
        ///
        /// 常见值：44100 Hz → 653 秒　　48000 Hz → 600 秒　　96000 Hz → 300 秒。
        /// 为什么不设更大：波形只记得住这么多，预录再长绿条也画不全、和波形对不上。
        /// 内存按「实际设置的秒数」分配、不按上限分配 —— 设 15 秒还是只占 15 秒。
        /// </summary>
        public static int MaxSecondsFor(int sampleRate)
        {
            return WaveformAnalyzer.SecondsCoveredAt(sampleRate);
        }

        private readonly object _lock = new object();
        private readonly int _blockAlign;   // 每帧字节数，保证永远按整帧对齐
        private byte[] _buffer;
        private int _writePos;
        private int _count;

        /// <summary>按时间（秒）、采样率、每帧字节数创建缓存</summary>
        public PrerollBuffer(int seconds, int sampleRate, int channels, int blockAlign)
        {
            _blockAlign = Math.Max(1, blockAlign);
            // 容量一律按「采样率 × 每帧字节数」算，不再写死 4 字节/样本。
            // 缓存里存的是采集出来的原始字节：32bit 浮点设备每样本 4 字节，
            // 但 16bit 设备只有 2 字节 —— 写死 4 会让标称秒数和实际缓存时长差一倍，
            // 时间轴绿条就只画得出真实预录长度的一半。
            Resize(BytesForSeconds(ClampSeconds(seconds, sampleRate), sampleRate, _blockAlign));
        }

        /// <summary>把秒数限制到 [0, 该采样率下的上限]</summary>
        public static int ClampSeconds(int seconds, int sampleRate)
        {
            if (seconds < 0) return 0;
            int max = MaxSecondsFor(sampleRate);
            return seconds > max ? max : seconds;
        }

        /// <summary>秒数 → 字节数（采样率 × 每帧字节数）</summary>
        public static int BytesForSeconds(int seconds, int sampleRate, int blockAlign)
        {
            long wanted = (long)Math.Max(0, seconds)
                          * Math.Max(1, sampleRate)
                          * Math.Max(1, blockAlign);
            if (wanted > int.MaxValue) wanted = int.MaxValue;
            return (int)wanted;
        }

        /// <summary>当前占用字节数</summary>
        public int Count
        {
            get { lock (_lock) { return _count; } }
        }

        /// <summary>已缓存的时长（秒）</summary>
        public double BufferedSeconds(int sampleRate)
        {
            lock (_lock)
            {
                int bytesPerSecond = Math.Max(1, sampleRate) * _blockAlign;
                return (double)_count / bytesPerSecond;
            }
        }

        /// <summary>
        /// 重建缓存（用户改了预录时长时调用）。
        /// 已有数据保留：
        ///   变大 → 全部留下，接着往后写；
        ///   变小 → 只留最新的那一段，最老的丢弃。
        /// </summary>
        public void Resize(int newCapacityBytes)
        {
            int aligned = Math.Max(_blockAlign, newCapacityBytes / _blockAlign * _blockAlign);

            lock (_lock)
            {
                // 容量没变就什么都不做。
                // 之前无条件重建：切换采样率时「秒数」变了但「字节数」往往相同
                // （秒数 × 采样率 是不变量），于是白分配一整块再整块拷一遍。
                // 600 秒 @48k 双声道 float 约 230MB，这一下就是白扔 230MB 内存带宽。
                if (_buffer != null && _buffer.Length == aligned) return;
            }

            // 大数组分配放在锁外：这么一块可能上百 MB，分配本身要走 GC（可能触发 LOH 压缩），
            // 耗时可观；如果还在锁里，实时音频线程的 Add 会被一起堵住 → 录音出现断音。
            var newBuf = new byte[aligned];

            lock (_lock)
            {
                byte[] old = _buffer;
                if (old != null && old.Length == aligned) return;   // 期间已被重建过

                int keep = old == null ? 0 : Math.Min(_count, aligned);
                keep = keep / _blockAlign * _blockAlign;   // 整帧对齐，绝不切开一帧

                if (keep > 0)
                {
                    int cap = old.Length;
                    int start = ((_writePos - keep) % cap + cap) % cap;
                    int first = Math.Min(keep, cap - start);
                    Buffer.BlockCopy(old, start, newBuf, 0, first);
                    if (keep > first)
                        Buffer.BlockCopy(old, 0, newBuf, first, keep - first);
                    _writePos = keep % aligned;
                    _count = keep;
                }
                else
                {
                    _writePos = 0;
                    _count = 0;
                }

                _buffer = newBuf;
            }
        }

        /// <summary>按秒数调整容量，保留已有数据</summary>
        public void SetSeconds(int seconds, int sampleRate)
        {
            Resize(BytesForSeconds(ClampSeconds(seconds, sampleRate), sampleRate, _blockAlign));
        }

        /// <summary>采集线程调用：写入一段数据（超出容量时自动丢弃最旧的）</summary>
        public void Write(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0) return;
            lock (_lock)
            {
                if (_buffer == null) return;
                int cap = _buffer.Length;
                if (cap <= 0) return;

                // 单次写入超过整个容量：只保留最后 capacity 字节
                if (count >= cap)
                {
                    int start = offset + count - cap;
                    start -= (start % _blockAlign);
                    int n = count - (start - offset);
                    n = n / _blockAlign * _blockAlign;
                    Buffer.BlockCopy(data, start, _buffer, 0, n);
                    _writePos = n % cap;
                    _count = Math.Min(n, cap);
                    return;
                }

                // 对齐到整帧
                count = count / _blockAlign * _blockAlign;
                if (count <= 0) return;

                int first = Math.Min(count, cap - _writePos);
                Buffer.BlockCopy(data, offset, _buffer, _writePos, first);
                if (count > first)
                    Buffer.BlockCopy(data, offset + first, _buffer, 0, count - first);

                _writePos = (_writePos + count) % cap;
                _count = Math.Min(cap, _count + count);
            }
        }

        /// <summary>把缓存内容按时间顺序（最旧的在前）拷出来；clear=true 时顺便清空</summary>
        private byte[] CopyOut_NoLock(bool clear)
        {
            if (_count <= 0)
            {
                if (clear) { _writePos = 0; _count = 0; }
                return Array.Empty<byte>();
            }

            int cap = _buffer.Length;
            var result = new byte[_count];
            int start = ((_writePos - _count) % cap + cap) % cap;
            int first = Math.Min(_count, cap - start);
            Buffer.BlockCopy(_buffer, start, result, 0, first);
            if (_count > first)
                Buffer.BlockCopy(_buffer, 0, result, first, _count - first);

            if (clear) { _writePos = 0; _count = 0; }
            return result;
        }

        /// <summary>
        /// 取出缓存区内的全部数据（按时间顺序，最旧的在前），并清空。
        /// 录制开始时调用一次，用于把「点击前的那段」接到文件开头。
        /// </summary>
        public byte[] DrainAll()
        {
            lock (_lock) { return CopyOut_NoLock(true); }
        }

        /// <summary>
        /// 复制出缓存区内的全部数据（按时间顺序，最旧的在前），不清空。
        /// 「保存预录」用它：存完文件，缓存继续往下填。
        /// </summary>
        public byte[] PeekAll()
        {
            lock (_lock) { return CopyOut_NoLock(false); }
        }

        /// <summary>清空缓存（如切换设备、手动重置预录）</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _writePos = 0;
                _count = 0;
            }
        }
    }
}
