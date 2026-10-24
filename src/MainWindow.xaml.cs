using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RecLite.Audio;
using RecLite.Core;
using RecLite.UI;

namespace RecLite
{
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly RecordingEngine _engine;
        private readonly DispatcherTimer _uiTimer;

        private List<AudioDeviceInfo> _sources = new List<AudioDeviceInfo>();
        private List<AudioDeviceInfo> _playbacks = new List<AudioDeviceInfo>();

        private DateTime _recordStart;
        private TimeSpan _pausedTotal;
        private DateTime _pausedAt;
        private TimeSpan _frozenElapsed;
        private bool _processing;
        private string _tempWav;
        private string _tempDir;
        private bool _suspendEvents;

        /// <summary>命令行 --play 指定的文件。只记路径，等设备就绪之后再真正播放。</summary>
        private string _startupPlayPath;

        /// <summary>窗口正在关闭：延后期里看到它就直接放弃后续动作，避免对已释放的引擎动手。</summary>
        private bool _closing;

        /// <summary>延后期是否已经跑过（防重复进入）。</summary>
        private bool _deferredDone;

        /// <summary>延后期结束的时间戳：卡顿日志是在阻塞结束、渲染恢复时才写的，必须按「间隔起点」判断。</summary>
        private long _deferredEndTicks;
        // 用户是否手动改过设备。
        // 没改过就一直跟随系统默认设备，绝不把设备 ID 写死进配置 ——
        // 否则换机器 / 换声卡 / 改了系统默认设备之后，程序还死记着旧的那个。

        private IWavePlayer _player;
        private WaveStream _reader;
        private VolumeSampleProvider _volumeProvider;
        /// <summary>
        /// 最近一次「可以再播一次」的文件。
        /// 录制保存的和拖进来 / 打开的文件都算 —— 它们本质都是「播放一个文件」，
        /// 所以只留这一个变量，试听按钮永远播它。
        /// </summary>
        private string _lastPlayablePath;
        private int _playToken;          // 播放令牌：防止异步加载的波形串台
        private bool _savePrerollBusy;   // 「存预录」正在后台落盘，防重复点击

        private TrayIcon _tray;          // 托盘图标（带 --no-tray 启动时为 null）

        // ---------- 下拉选项模型 ----------
        private class FormatOption
        {
            public string Format;
            public int Bits;
            public int Bitrate;
            public string Label;
            public override string ToString() => Label;
        }

        private class RateOption
        {
            public int Rate;
            public string Label;
            public override string ToString() => Label;
        }

        private class PrerollOption
        {
            public int Seconds;
            public string Label;
            public override string ToString() => Label;
        }

        public MainWindow()
        {
            InitializeComponent();

            // 命令行 --play <file>：启动后自动加载并播放，方便自动化回归测试。
            // 只记下路径 —— 设备还没打开就播放必然失败，真正的播放交给 RunDeferredStartup 收尾。
            try
            {
                var a = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(a, "--play");
                if (i >= 0 && i + 1 < a.Length && File.Exists(a[i + 1]))
                    _startupPlayPath = a[i + 1];
            }
            catch { }

            // 托盘：等窗口真正显示之后再放图标。
            // 构造函数阶段 Application 的消息泵还没真正跑起来，Shell_NotifyIcon 有可能直接失败，
            // 而它不回报失败 —— 只能靠「晚一点再放」来规避这个时机问题。
            Loaded += (s, e) => InitTray();

            // 时间轴常开，贴在波形下方；dB 刻度条独立在左侧
            if (Timeline != null && Waveform != null) Timeline.Attach(Waveform);
            if (DbScale != null && Waveform != null) DbScale.Attach(Waveform);

            // 试听时在波形上点击 / 拖动 → 从这个位置开始播
            Waveform.SeekRequested += (ratio, commit) =>
            {
                if (_reader == null) return;
                try
                {
                    double total = _reader.TotalTime.TotalSeconds;
                    if (total <= 0.01) return;

                    double pos = total * ratio;
                    if (pos < 0) pos = 0;
                    if (pos > total) pos = total;

                    // 先只移动黄条（拖动过程中不碰播放器，避免和输出线程抢文件流）
                    _playSmoothPos = pos;
                    ReanchorPlayhead();
                    Waveform.SetPlayProgress(pos / total);

                    if (!commit) return;

                    // 松手才真正 seek。
                    // 不在 UI 线程碰 _player（Stop/Pause 都会等播放线程，会死锁），
                    // 只把请求交给 MeteringProvider，由音频线程在 Read 里执行。
                    if (_previewMeter != null) _previewMeter.RequestSeek(pos);
                    _playSmoothPos = pos;
                    ReanchorPlayhead();
                }
                catch (Exception ex) { Log.Error("Seek", ex); }
            };

            Log.Write("=== RecLite 启动 === BaseDir=" + AppPaths.BaseDir);
            _settings = AppSettings.Load();
            Log.Write("录音目录 = " + _settings.GetRecordingsDir());

            // 恢复上次的窗口位置与大小（越界就退回居中，避免窗口跑到看不见的屏幕上）
            try
            {
                if (_settings.WindowWidth > 200 && _settings.WindowHeight > 200)
                {
                    Width = _settings.WindowWidth;
                    Height = _settings.WindowHeight;

                    double vl = SystemParameters.VirtualScreenLeft;
                    double vt = SystemParameters.VirtualScreenTop;
                    double vw = SystemParameters.VirtualScreenWidth;
                    double vh = SystemParameters.VirtualScreenHeight;

                    double l = _settings.WindowLeft;
                    double t = _settings.WindowTop;
                    bool ok = l >= vl - 50 && l <= vl + vw - 150
                           && t >= vt - 50 && t <= vt + vh - 150;
                    if (ok)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = l;
                        Top = t;
                    }
                }
            }
            catch { }

            // 确保 WPF 的临时目录存在。
            // WPF 加载新窗口时会在 %TEMP%\WPF 下写临时文件，如果该目录被清理工具删掉，
            // WPF 不会自动重建，导致「对路径 ...Temp\WPF\xxx 的访问被拒绝」。
            try
            {
                var wpfTemp = Path.Combine(Path.GetTempPath(), "WPF");
                if (!Directory.Exists(wpfTemp)) Directory.CreateDirectory(wpfTemp);
                Log.Write("WPF 临时目录 = " + wpfTemp);
            }
            catch (Exception ex) { Log.Error("EnsureWpfTemp", ex); }

            _engine = new RecordingEngine();
            _engine.Error += msg => Dispatcher.BeginInvoke(new Action(() => SetStatus("⚠ " + msg)));

            BuildOptionLists();
            ApplyNonDeviceSettings();

            // 设备相关的初始化要等窗口可见之后再做（A 方案：先开门、后背菜）：
            // 扫残留临时文件 + 枚举所有设备 + 打开采集设备，这三件活要吃掉 100~200 ms，
            // 却都不是「把窗口画出来」所必需的。
            //
            // 「正在连接设备…」与临时置灰必须在这里（Show 之前）设好 —— 它们要出现在第一帧上，
            // 用户才知道程序在忙。等延后期里再设就晚了：状态改动与重活挤在同一个时间片里，WPF 来不及重绘。
            EnterConnectingUiState();

            // ContentRendered 而不是 Loaded：Loaded 在第一帧画出来之前就会触发，等于没挪。
            // 优先级用 Normal 而不是 Background：本仓库踩过「Background 被 Render 长期饿死」的坑
            // （见 OnRendering 上方的注释）；此时首帧已经渲染完成，不需要再给渲染让路。
            ContentRendered += (s, e) =>
            {
                StartupTrace.MarkWindowVisible();
                Dispatcher.BeginInvoke(new Action(RunDeferredStartup), DispatcherPriority.Normal);
            };

            // 60fps：WPF 渲染被 vsync 锁在 60Hz，更高频率只会空转（实测 180fps 设定仍跑 ~64fps）
            //
            // 【必须显式给 Render 优先级，这是踩过坑的】
            // DispatcherTimer 的默认优先级是 Background —— 最低的几档之一。
            // 而 CompositionTarget.Rendering 是 Render 优先级、且每帧都触发
            // （高刷屏下实测 220 次/秒）。于是 Background 优先级的定时器长期排不上队，
            // 表现就是：波形突然停住两三秒甚至更久，然后突然跳一大段恢复。
            // 日志佐证：出问题时「每 5 秒一条的性能日志」直接断了 3 分 41 秒。
            _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            // 波形动画改用「与屏幕刷新同步」的 Rendering 事件驱动。
            // DispatcherTimer 走系统时钟（15.6ms 精度）且与 vsync 不同步，
            // 会导致每帧前进量忽大忽小 —— 那正是"跳帧感"的来源。
            CompositionTarget.Rendering += OnRendering;

            Closed += (s, e) =>
            {
                // 立在最前面：延后期可能还排在队列里（用户在设备就绪前就关窗），
                // 它一看到这个标志就放弃 —— 否则会去 StartDevice 一台已经被 Dispose 的引擎。
                _closing = true;
                try { CompositionTarget.Rendering -= OnRendering; } catch { }
                try { SaveSettingsFromUi(); } catch { }   // 退出时保存全部设置
                StopPlayback();
                try { _engine.Dispose(); } catch { }
                if (_tempWav != null && File.Exists(_tempWav)) { try { File.Delete(_tempWav); } catch { } }
                // 关得比设备就绪还快的那次，延后期不会 Flush —— 这里补一次，
                // Flush 自带幂等守卫，重复调用不会多写一行。
                StartupTrace.Flush();
                Log.Write("=== RecLite 退出 ===");
            };
        }

        // ---------- 与屏幕刷新同步的波形动画 ----------
        private long _lastRenderTicks;
        private long _lastStutterLogTicks;                                          // 上次记录卡顿的时间（防止刷屏）
        private long _maxDeltaTicks = System.Diagnostics.Stopwatch.Frequency / 10;   // 单帧时间上限 100ms

        // 说明：这里**不做时间限帧**。
        //
        // 之前试过「按时间把帧率压到 60fps」，实测反而更顿 —— 因为屏幕刷新率不是 60 的整数倍时，
        // 帧间隔只能在「3 拍 / 4 拍」之间来回跳，位移量跟着变成 1 像素 / 2 像素交替，肉眼就是一顿一顿。
        //
        // 现在改成**按像素驱动**：每个渲染拍都推进显示位置，但只有在「跨过整数个波形点」时
        // 才真正重绘（见 WaveformControl.TickAnimation）。一个点正好 1 像素，
        // 于是每次重绘画面的位移永远恰好 1 像素，重绘频率自然等于数据速率
        // （48000Hz ÷ 800 采样/点 = 60 次/秒），与屏幕刷新率无关。
        //
        // 关于垂直同步：WPF 的 Rendering 事件本身就挂在屏幕刷新的节拍上（内建 vsync），
        // 没有"开/关垂直同步"的开关。
        private void OnRendering(object sender, EventArgs e)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastRenderTicks == 0) { _lastRenderTicks = now; return; }

            long rawDelta = now - _lastRenderTicks;
            _lastRenderTicks = now;

            // 把一次超大间隔（启动首帧、窗口最小化、切桌面回来）钳到 100ms，
            // 否则这坨时间会一次性灌进动画，画面会突然窜一大段。
            long deltaTicks = rawDelta > _maxDeltaTicks ? _maxDeltaTicks : rawDelta;

            double dt = deltaTicks / (double)System.Diagnostics.Stopwatch.Frequency;
            if (dt > 0) Waveform.TickAnimation(dt);

            // 卡顿记录：只对「真实」帧间隔判断，并且 2 秒内最多写一条，免得刷屏
            if (rawDelta > _maxDeltaTicks && now - _lastStutterLogTicks > System.Diagnostics.Stopwatch.Frequency * 2)
            {
                _lastStutterLogTicks = now;
                // 启动期的这些大间隔是「先出窗后连设备」的必然产物，不标注的话，
                // 以后排查真实卡顿会被每次启动自带的这一条误导。
                string note = "";
                if (_deferredEndTicks == 0) note = "（启动期，预期内）";
                else if ((now - rawDelta) <= _deferredEndTicks) note = "（延后期，预期内）";
                Log.Write(string.Format("[卡顿] 帧间隔 {0:0} ms{1}",
                    rawDelta * 1000.0 / System.Diagnostics.Stopwatch.Frequency, note));
            }

        }

        // ==================== 托盘 ====================

        // 托盘图标是**常驻入口**：程序一启动就放进通知区，退出时才摘掉。
        // 不做「最小化就隐藏窗口」：那会让任务栏按钮消失，而 .NET 的 NotifyIcon 又不回报
        // 图标到底有没有画出来（这台机器上 UI 自动化连图标都找不到），等于把唯一入口赌在
        // 一个看不见的东西上。现在最小化 = 普通最小化（任务栏按钮一直在），托盘只是多给一条路。
        // 另外 App 里还有单实例唤起：再双击一次 RecLite.exe 也能把窗口叫回来。

        private WindowState _lastNormalWindowState = WindowState.Normal;

        /// <summary>把窗口叫回来（托盘双击/菜单、App 单实例唤起共用）</summary>
        public void RestoreFromTray()
        {
            try
            {
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = _lastNormalWindowState;
                Activate();
                Log.Write("恢复窗口（托盘图标或第二次启动）");
            }
            catch (Exception ex) { Log.Error("RestoreFromTray", ex); }
        }

        /// <summary>
        /// 装上常驻托盘图标。
        /// 自动化脚本用 --no-tray 启动：否则托盘会多一个按钮、也会拖慢脚本启动。
        /// </summary>
        private void InitTray()
        {
            try
            {
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--no-tray") >= 0) return;

                _tray = new TrayIcon(this, ExitFromTray);

                // 等窗口画完再放图标。Show() 会用 NotifyIcon.added（NIM_ADD 的返回值）
                // 判断是否真的登记成功，所以这里不用再靠「延后一拍猜时机」。
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_tray != null && _tray.Show() == TrayIcon.TrayShowResult.NotRegistered)
                        {
                            Log.Write("托盘图标登记失败（NIM_ADD 未成功，点「缩到托盘」会被拦下）");
                        }
                        _tray?.SetText("RecLite 轻录音");
                    }
                    catch (Exception ex) { Log.Error("TrayShowDelayed", ex); }
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                // 记住最小化之前是「普通」还是「最大化」，恢复时还原成原来那样
                StateChanged += (s, e) =>
                {
                    if (WindowState != WindowState.Minimized) _lastNormalWindowState = WindowState;
                };

                // 关窗退出时摘掉图标，避免留下点不动的死图标
                Closed += (s, e) =>
                {
                    if (_tray != null) { _tray.Dispose(); _tray = null; }
                };
            }
            catch (Exception ex) { Log.Error("InitTray", ex); }
        }

        /// <summary>
        /// 单独按钮：「缩到托盘」。
        /// 标题栏那个最小化按钮保持普通最小化（任务栏按钮还在），只有点这个才把窗口藏进托盘。
        /// </summary>
        private void TrayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_tray == null)
            {
                SetStatus("托盘图标不可用，窗口保留在这里");
                return;
            }
            try
            {
                // 一律允许隐藏 —— 按钮必须能用。
                // 之前拿反查结果做硬性拦截，结果「图标明明在」也被拦住，等于把功能弄坏了。
                // 万一图标真的没登记上，还有单实例唤起这条路：
                // 再启动一次 RecLite.exe 会请求已有实例把窗口叫回来（App.xaml.cs）。
                var st = _tray.Show();

                Hide();
                Log.Write("点「缩到托盘」隐藏窗口（图标状态=" + st + "）");

                if (st == TrayIcon.TrayShowResult.NotRegistered)
                {
                    // 没有可靠证据证明图标在，就把找回窗口的办法直接告诉用户
                    SetStatus("已缩到托盘；若看不到图标，再启动一次 RecLite.exe 即可把窗口叫回来");
                }
            }
            catch (Exception ex) { Log.Error("TrayBtn", ex); }
        }

        /// <summary>托盘菜单的「退出」：录音中先确认，再延后一拍关闭程序</summary>
        private void ExitFromTray()
        {
            try
            {
                if (_engine != null && _engine.IsRecording)
                {
                    var r = MessageBox.Show(
                        "正在录音，退出会丢掉这一段录音。\n\n确定要退出吗？",
                        "RecLite 轻录音", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                    if (r != MessageBoxResult.Yes) return;
                }

                // WinForms 的菜单回调跑在它自己的嵌套消息循环里，直接 Shutdown 会让菜单卡住、
                // 进程晚退；延后一拍交给 WPF 的 Dispatcher 再关
                Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
            }
            catch (Exception ex) { Log.Error("ExitFromTray", ex); }
        }

        private void CleanupStaleTemp()
        {
            try
            {
                var dir = _settings.GetRecordingsDir();
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir, "*.tmp.wav"))
                {
                    try
                    {
                        if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromMinutes(10))
                            File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ==================== 初始化 ====================

        private void BuildOptionLists()
        {
            // 上限不写死：由「波形点数上限 ÷ 每秒点数」现算，见 WaveformAnalyzer.SecondsCoveredAt。
            // 取样入口统一走 PrerollMaxSeconds（→ RecordingEngine.MaxPrerollSeconds），别处不要自己算。
            // 用 ObservableCollection：手打的自定义秒数要动态插一个占位项进来。
            PrerollCombo.ItemsSource = new ObservableCollection<PrerollOption>
            {
                new PrerollOption{ Seconds=0,   Label="关闭" },
                new PrerollOption{ Seconds=15,  Label="15 秒" },
                new PrerollOption{ Seconds=30,  Label="30 秒" },
                new PrerollOption{ Seconds=45,  Label="45 秒" },
                new PrerollOption{ Seconds=60,  Label="60 秒" },
                new PrerollOption{ Seconds=90,  Label="90 秒" },
                new PrerollOption{ Seconds=120, Label="120 秒" },
            };

            UpdatePrerollToolTip();

            RateCombo.ItemsSource = new List<RateOption>
            {
                new RateOption{ Rate=0,    Label="跟随设备" },
                new RateOption{ Rate=44100,Label="44100 Hz" },
                new RateOption{ Rate=48000,Label="48000 Hz" },
                new RateOption{ Rate=88200,Label="88200 Hz" },
                new RateOption{ Rate=96000,Label="96000 Hz" },
            };

            var hasMp3 = Mp3Converter.IsAvailable();
            var fmts = new List<FormatOption>
            {
                new FormatOption{ Format="wav", Bits=16, Label="WAV · 16 bit" },
                new FormatOption{ Format="wav", Bits=24, Label="WAV · 24 bit" },
                new FormatOption{ Format="wav", Bits=32, Label="WAV · 32 bit 浮点" },
            };
            if (hasMp3)
            {
                fmts.Add(new FormatOption { Format = "mp3", Bits = 16, Bitrate = 128, Label = "MP3 · 128 kbps" });
                fmts.Add(new FormatOption { Format = "mp3", Bits = 16, Bitrate = 192, Label = "MP3 · 192 kbps" });
                fmts.Add(new FormatOption { Format = "mp3", Bits = 16, Bitrate = 256, Label = "MP3 · 256 kbps" });
                fmts.Add(new FormatOption { Format = "mp3", Bits = 16, Bitrate = 320, Label = "MP3 · 320 kbps" });
            }
            FormatCombo.ItemsSource = fmts;

            NameModeCombo.ItemsSource = new List<string> { "自动命名", "停止时询问" };
        }

        private void LoadDevices()
        {
            _suspendEvents = true;
            try
            {
                _sources = DeviceService.GetRecordingSources();
                _playbacks = DeviceService.GetPlaybackDevices();

                CaptureCombo.ItemsSource = _sources;
                CaptureCombo.DisplayMemberPath = "DisplayName";
                PlaybackCombo.ItemsSource = _playbacks;
                PlaybackCombo.DisplayMemberPath = "DisplayName";
            }
            finally { _suspendEvents = false; }
        }

        /// <summary>
        /// 不依赖设备列表的那部分设置。构造期就要生效 —— 用户第一眼看到的
        /// 采样率 / 格式 / 目录 / 音量都来自这里，不能等设备枚举完才算数。
        /// </summary>
        private void ApplyNonDeviceSettings()
        {
            _suspendEvents = true;
            try
            {
                SyncPrerollDisplay();

                var rates = RateCombo.ItemsSource as List<RateOption>;
                RateCombo.SelectedItem = rates?.FirstOrDefault(x => x.Rate == _settings.SampleRate) ?? rates?.FirstOrDefault();

                var fmts = FormatCombo.ItemsSource as List<FormatOption>;
                FormatOption sel = null;
                if (_settings.OutputFormat == "mp3")
                    sel = fmts?.FirstOrDefault(x => x.Format == "mp3" && x.Bitrate == _settings.Mp3Bitrate);
                else
                    sel = fmts?.FirstOrDefault(x => x.Format == "wav" && x.Bits == _settings.BitsPerSample);
                FormatCombo.SelectedItem = sel ?? fmts?.FirstOrDefault();

                NameModeCombo.SelectedIndex = _settings.FileNameMode == "ask" ? 1 : 0;
                PrefixBox.Text = _settings.FileNamePrefix;
                DirBox.Text = _settings.GetRecordingsDir();

                // 音量（控件自己管显示，不用再同步文本）
                // 上限交给控件的 MaxPercent 兜底，这里不要写死数字 ——
                // 原来写死 Math.Min(2, ...)，就算 UI 上限提到 300%，
                // 重启后设置也会被悄悄夹回 200%
                RecordVolGauge.Value = Math.Max(0, _settings.InputGain);
                PlayVolGauge.Value = Math.Max(0, Math.Min(2, _settings.PlaybackVolume));
                _engine.InputGain = (float)RecordVolGauge.Value;

                // 开关
                IdleWaveCheck.IsChecked = !_settings.HideWaveformIdle;
                if (Waveform != null) Waveform.UseLogScale = _settings.WaveLogScale;
                UpdateScaleBtn();

            }
            finally { _suspendEvents = false; }
        }

        /// <summary>
        /// 选哪个设备。依赖 LoadDevices() 填好的 _sources / _playbacks，
        /// 所以只能在设备枚举之后跑（A 方案的延后期）。
        /// </summary>
        private void ApplyDeviceSelection()
        {
            _suspendEvents = true;
            try
            {
                // 设备
                if (!string.IsNullOrEmpty(_settings.CaptureDeviceId))
                    CaptureCombo.SelectedItem = _sources.FirstOrDefault(x => x.Id == _settings.CaptureDeviceId);
                if (CaptureCombo.SelectedItem == null)
                {
                    // 这里只取 Id 用，取完必须释放：MMDevice 是 COM 包装，
                    // 每次应用设置都漏两个的话，反复切设置会一直堆积引用。
                    var defCap = DeviceService.GetDefaultCapture();
                    try
                    {
                        var id = TryGetId(defCap);
                        if (id != null) CaptureCombo.SelectedItem = _sources.FirstOrDefault(x => x.Id == id);
                    }
                    finally { if (defCap != null) { try { defCap.Dispose(); } catch (Exception) { } } }
                }
                if (CaptureCombo.SelectedItem == null)
                    CaptureCombo.SelectedItem = _sources.FirstOrDefault(x => x.Kind == DeviceKind.Capture) ?? _sources.FirstOrDefault();

                if (!string.IsNullOrEmpty(_settings.PlaybackDeviceId))
                    PlaybackCombo.SelectedItem = _playbacks.FirstOrDefault(x => x.Id == _settings.PlaybackDeviceId);
                if (PlaybackCombo.SelectedItem == null)
                {
                    var defPlay = DeviceService.GetDefaultRender();
                    try
                    {
                        var id = TryGetId(defPlay);
                        if (id != null) PlaybackCombo.SelectedItem = _playbacks.FirstOrDefault(x => x.Id == id);
                    }
                    finally { if (defPlay != null) { try { defPlay.Dispose(); } catch (Exception) { } } }
                }
                if (PlaybackCombo.SelectedItem == null)
                    PlaybackCombo.SelectedItem = _playbacks.FirstOrDefault();
            }
            finally { _suspendEvents = false; }
        }

        /// <summary>
        /// 设备初始化前的临时状态：状态栏提示 + 三个控件禁用 + 面板调暗。
        /// 调暗值（0.4）与 SetRecordingUi 的「不可用」取值保持一致 —— 暗色主题下光靠 IsEnabled
        /// 肉眼看不出差别，用户会去点一个「看起来还能用」的控件。
        /// 单独成方法，是因为它必须在 Show 之前调用（否则来不及画上第一帧）。
        /// </summary>
        private void EnterConnectingUiState()
        {
            SetStatus("正在连接设备…");
            CaptureCombo.IsEnabled = false;
            PlaybackCombo.IsEnabled = false;
            RecordBtn.IsEnabled = false;
            if (CapturePanel != null) CapturePanel.Opacity = 0.4;
        }

        /// <summary>
        /// 窗口已经可见之后才做的初始化（A 方案：先开门、后背菜）。
        ///
        /// 这一段里的三件活都不是「把窗口画出来」所必需的，却要吃掉 100~200 ms：
        /// 扫录音目录的残留临时文件、枚举所有设备、打开采集设备并启动预录缓存。
        /// 放在 Show() 之前，就等于让用户对着空气等。
        /// </summary>
        private async void RunDeferredStartup()
        {
            bool opened = false;
            try
            {
                if (_deferredDone || _closing) return;
                _deferredDone = true;

                // 关键的一步：把控制权交还给渲染，让「正在连接设备…」与置灰真正画上屏，
                // 再开始干重活。少了这一步，状态改动与阻塞操作挤在同一个时间片里，
                // 用户看到的是「窗口出来了、完全不响应、也没有任何文字解释」。
                await Dispatcher.Yield(DispatcherPriority.Render);
                if (_closing) return;

                try { CleanupStaleTemp(); }
                catch (Exception ex) { Log.Error("CleanupStaleTemp", ex); }

                LoadDevices();
                ApplyDeviceSelection();
                OpenSelectedDevice();
                opened = true;
            }
            catch (Exception ex)
            {
                // 延后期炸了，不能让界面永远停在「正在连接设备…」
                Log.Error("DeferredStartup", ex);
                SetStatus("⚠ 设备初始化失败：" + ex.Message);
            }
            finally
            {
                if (!_closing)
                {
                    // 恢复可用状态。这里刻意与改造前保持一致：设备打不开时点录音会给出提示，
                    // 而不是无声无息地按不动 —— 只为提速而改变失败路径，回归面不值当。
                    CaptureCombo.IsEnabled = true;
                    PlaybackCombo.IsEnabled = true;
                    RecordBtn.IsEnabled = true;
                    if (CapturePanel != null) CapturePanel.Opacity = 1.0;
                }

                StartupTrace.MarkDeviceReady();
                StartupTrace.Flush();
                _deferredEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();

                // 只在设备真的打开之后才试听：失败时状态栏刚写了红字原因，播放会把它盖掉。
                if (opened && _startupPlayPath != null && !_closing)
                {
                    string path = _startupPlayPath;
                    _startupPlayPath = null;
                    try { PlayFile(path); }
                    catch (Exception ex) { Log.Error("--play", ex); }
                }
            }
        }

        // ==================== 设备 ====================

        /// <summary>
        /// 允许的最大录制声道数。
        ///
        /// 预录缓存占的内存 = 秒数 × 采样率 × 声道数 × 每样本字节数，
        /// 2 声道 48kHz 设满上限约 230MB，8 声道同样设置要接近 1GB —— 不能让它发生。
        /// 现在波形、电平表也按实际声道数分带，超过 2 声道在界面上本来也展示不好。
        /// </summary>
        private const int MaxRecordChannels = 2;

        /// <summary>上一次成功打开的录制通道 Id。被拒绝时用它把下拉框退回去。</summary>
        private string _lastGoodCaptureId;

        /// <summary>
        /// 正在执行「退回可用通道」。
        /// 用来切断「回退 → 重开 → 又超限 → 再回退」的递归：
        /// 如果某台设备 MixFormat 报 2 声道、实际打开却是多声道，
        /// 回退选中的它还会超限，没有这个闸就是无限递归 → StackOverflow。
        /// </summary>
        private bool _revertingCapture;

        private AudioDeviceInfo CurrentSource => CaptureCombo.SelectedItem as AudioDeviceInfo;

        /// <summary>
        /// 把录制通道下拉框退回一个可用通道（2 声道以内）。
        ///
        /// 只改显示、不触发 SelectionChanged（靠 _suspendEvents 挡住）；
        /// reopen=true 时才真的重新打开 —— 用于「已经打开了才发现超限」的回滚场景。
        /// </summary>
        private void RevertCaptureCombo(bool reopen)
        {
            // 已经在回退过程中又失败了：说明找不到真正可用的通道，停手，别再往下递归
            if (_revertingCapture)
            {
                Log.Write("[通道] 回退后仍然超限，停止重试");
                SetStatus("没有可用的 " + MaxRecordChannels + " 声道以内录制通道");
                return;
            }

            AudioDeviceInfo back = null;
            if (_sources != null)
            {
                back = _sources.FirstOrDefault(x => x.Id == _lastGoodCaptureId
                                                    && x.Channels > 0 && x.Channels <= MaxRecordChannels);
                if (back == null)
                    back = _sources.FirstOrDefault(x => x.Channels > 0 && x.Channels <= MaxRecordChannels);
            }

            if (back == null)
            {
                SetStatus("没有可用的 " + MaxRecordChannels + " 声道以内录制通道");
                return;
            }

            bool old = _suspendEvents;
            _suspendEvents = true;
            try { CaptureCombo.SelectedItem = back; }
            finally { _suspendEvents = old; }

            // 配置也要一起退回去：SelectionChanged 在此之前已经把被拒绝的设备写进配置了，
            // 不回滚的话下次启动会去开那台用不了的设备
            if (_settings != null)
            {
                _settings.CaptureDeviceId = back.Id;
                _settings.CaptureFromRenderLoopback = back.Kind == DeviceKind.Loopback;
                _settings.Save();
            }

            Log.Write("[通道] 已退回可用通道：" + back.Name + "（" + back.Channels + " 声道）");

            if (reopen)
            {
                _revertingCapture = true;
                try { OpenSelectedDevice(); }
                finally { _revertingCapture = false; }
            }
        }

        private static string TryGetId(MMDevice d)
        {
            if (d == null) return null;
            try { return d.ID; } catch (Exception) { return null; }
        }

        private void OpenSelectedDevice()
        {
            var src = CurrentSource;
            if (src == null) { SetStatus("未找到可用录音设备"); return; }

            try
            {
                // 录音中禁止切通道（下拉框本身也会被禁用，这里再兜一层，防止内部路径绕过）
                if (_engine.IsRecording)
                {
                    SetStatus("录音中不能切换录制通道，请先停止录音");
                    RevertCaptureCombo(false);
                    return;
                }

                var dev = DeviceService.FindById(src.Id);
                if (dev == null) { SetStatus("设备不可用：" + src.Name); return; }

                _engine.StartDevice(dev, src.Kind, _settings.PrerollSeconds);

                // 第二道防线：设备信息里的声道数可能读不准（MixFormat 读取失败时是 0），
                // 真实打开后一旦超限就立刻停掉并退回 —— 绝不能让缓存按 8 声道分配出去
                if (_engine.Channels > MaxRecordChannels)
                {
                    int actual = _engine.Channels;
                    _engine.StopDevice();
                    string msg = string.Format("「{0}」实际是 {1} 声道，超过 {2} 声道上限，已停止使用",
                        src.Name, actual, MaxRecordChannels);
                    Log.Write("[通道] 拒绝（打开后复检）：" + msg);
                    SetStatus(msg);
                    RevertCaptureCombo(true);   // 设备已被停掉，必须把原来那个重新打开
                    return;
                }

                Log.Write(string.Format("设备已打开: {0} [{1}] {2}Hz {3}ch {4}bit 预录={5}秒",
                    src.Name, src.Kind, _engine.SampleRate, _engine.Channels, _engine.BitsPerSample, _settings.PrerollSeconds));

                CaptureInfo.Text = src.FormatSummary;
                Waveform.Configure(_engine.Channels);
                Waveform.Clear();
                Waveform.SetScrollSpeed(_engine.SampleRate, WaveformAnalyzer.SamplesPerPeak);
                // 电平表与削波灯按实际声道数裂成 L / R
                int chans = _engine.Channels;
                if (InMeter != null) { InMeter.ChannelCount = chans; InMeter.Reset(); }
                if (OutMeter != null) { OutMeter.ChannelCount = chans; OutMeter.Reset(); }
                if (InClipLight != null) { InClipLight.ChannelCount = chans; InClipLight.Reset(); }
                if (OutClipLight != null) { OutClipLight.ChannelCount = chans; OutClipLight.Reset(); }
                RefreshClipScale();
                UpdatePrerollToolTip();   // 采样率变了，上限跟着变，悬浮说明要刷一遍
                _lastGoodCaptureId = src.Id;   // 记下来，下次遇到超限通道时用它把下拉框退回去
                SetStatus("设备已就绪");
            }
            catch (Exception ex)
            {
                Log.Error("OpenSelectedDevice", ex);
                SetStatus("打开设备失败：" + ex.Message);
            }
        }

        private void CaptureCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suspendEvents) return;
            var src = CurrentSource;
            if (src == null) return;

            // 声道数上限：先把超限的挡在门外，别等它把预录缓存分配出去再回滚
            if (src.Channels > MaxRecordChannels)
            {
                string msg = string.Format("「{0}」是 {1} 声道，超过 {2} 声道上限，无法使用",
                    src.Name, src.Channels, MaxRecordChannels);
                Log.Write("[通道] 拒绝：" + msg);
                SetStatus(msg);
                RevertCaptureCombo(false);   // 原设备没动过，只需把下拉框退回去
                return;
            }

            _settings.CaptureDeviceId = src.Id;
            _settings.CaptureFromRenderLoopback = src.Kind == DeviceKind.Loopback;
            _settings.Save();
            OpenSelectedDevice();
        }

        private void PlaybackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suspendEvents) return;
            var d = PlaybackCombo.SelectedItem as AudioDeviceInfo;
            if (d != null) { _settings.PlaybackDeviceId = d.Id; _settings.Save(); }
        }

        private void NameModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suspendEvents) return;
            _settings.FileNameMode = NameModeCombo.SelectedIndex == 1 ? "ask" : "auto";
            _settings.Save();
        }

        // ==================== 预录 ====================

        /// <summary>手打的自定义秒数占位项（值不在固定下拉列表里时才存在）</summary>
        private PrerollOption _customPreroll;

        /// <summary>
        /// 预录秒数的上限（秒）—— 界面所有地方都从这里取，别处不要自己算。
        /// 采样率只有一个权威来源（RecordingEngine.SampleRate），这里只是转发；
        /// 引擎还没建好时按 48kHz 兜底。
        /// </summary>
        private int PrerollMaxSeconds
        {
            get
            {
                return _engine != null ? _engine.MaxPrerollSeconds : PrerollBuffer.MaxSecondsFor(48000);
            }
        }

        /// <summary>
        /// 更新预录秒数框的悬浮说明。
        ///
        /// 上限是动态的：波形最多记 MaxPoints 个点，每秒点数 = 采样率 ÷ SamplesPerPeak，
        /// 所以采集率越高、同样多的点能覆盖的秒数越少。换设备后要重新刷一遍说明。
        /// </summary>
        private void UpdatePrerollToolTip()
        {
            if (PrerollCombo == null) return;

            int sr = _engine != null && _engine.SampleRate > 0 ? _engine.SampleRate : 48000;
            PrerollCombo.ToolTip = string.Format(
                "双击可直接输入秒数（0 – {0}，0 = 关闭）\n"
                + "上限随设备采样率自动变：当前 {1} Hz 下，波形最多记住 {0} 秒\n"
                + "（采集率越高，同样多的波形点能覆盖的秒数越少）",
                PrerollMaxSeconds, sr);
        }

        /// <summary>
        /// 解析预录输入 → 秒数。支持「30」「30 秒」「30秒」「2 分」「关闭」「0」等写法。
        /// 超过上限（按当前设备采样率算出来）自动夹到上限。
        /// </summary>
        private bool TryParsePreroll(string text, out int seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = text.Trim();
            if (s == "关闭" || s == "关" || s == "无") return true;   // → 0

            // 「2分30秒」「1m30s」这类混合写法要单独算：
            // 否则去掉单位后会拼成 "230" 再乘 60，得到 120（还被上限夹住），完全不对。
            var mix = Regex.Match(s, @"^(\d+)\s*(?:分钟|分|m|M)\s*(\d+)\s*(?:秒|s|S)?$");
            if (mix.Success)
            {
                long mixed = long.Parse(mix.Groups[1].Value) * 60 + long.Parse(mix.Groups[2].Value);
                seconds = (int)Math.Min(mixed, PrerollMaxSeconds);
                return true;
            }

            bool isMinute = s.Contains("分");
            s = s.Replace("分钟", "").Replace("分", "")
                 .Replace("秒", "")
                 .Replace("s", "").Replace("S", "")
                 .Replace("m", "").Replace("M", "")
                 .Trim();

            int n;
            if (!int.TryParse(s, out n)) return false;
            if (n < 0) return false;

            // 用 long 算再夹上限：直接 n*60 在超大输入时 int 会溢出成负数
            long total = isMinute ? (long)n * 60 : n;
            seconds = (int)Math.Min(total, PrerollMaxSeconds);
            return true;
        }

        /// <summary>
        /// 让某个秒数生效：写配置 → 落盘 → 调引擎 → 回填界面。
        /// 这是预录设置的唯一生效入口（下拉选择、手动输入、非法回退都走它）。
        /// </summary>
        private void ApplyPrerollSeconds(int seconds)
        {
            if (_settings == null) return;

            // 录音中不能改预录时长：引擎那边会直接忽略（避免丢数据），
            // 如果这里还照常写配置、刷界面，就会出现「界面显示 60 秒、缓存实际还是 15 秒」的假象。
            if (_engine != null && _engine.IsRecording)
            {
                SetStatus("录音中不能修改预录时长，请先停止录音");
                SyncPrerollDisplay();   // 输入框/下拉框退回当前真实值
                return;
            }

            int sec = Math.Min(Math.Max(0, seconds), PrerollMaxSeconds);

            _settings.PrerollSeconds = sec;
            _settings.Save();
            try { _engine.SetPrerollSeconds(sec); } catch { }

            SyncPrerollDisplay();

            Log.Write("预录设置改为 " + sec + " 秒");
        }

        /// <summary>
        /// 把设置里的预录秒数同步到下拉框显示。
        ///
        /// 手打的自定义值（如 77）不在固定列表里，必须插一个占位项进去 ——
        /// ComboBox 只读模式下的 Text 是只读属性，直接赋值无效，会显示成空白。
        /// </summary>
        private void SyncPrerollDisplay()
        {
            if (_settings == null) return;
            var list = PrerollCombo.ItemsSource as ObservableCollection<PrerollOption>;
            if (list == null) return;

            int sec = Math.Min(Math.Max(0, _settings.PrerollSeconds), PrerollMaxSeconds);

            // 排除占位项自己，避免它残留的旧秒数被误当成「标准选项」
            var hit = list.FirstOrDefault(x => !ReferenceEquals(x, _customPreroll) && x.Seconds == sec);

            if (hit == null)
            {
                // 占位项：整个换新的，而不是改 Label 再刷新。
                // 改 Label 需要 PrerollCombo.Items.Refresh()，而 Refresh 会把 SelectedItem 冲掉，
                // 导致选中项乱跳 —— 实测启动时被连着写成「0 秒」再「15 秒」，配置就被改坏了。
                if (_customPreroll != null) list.Remove(_customPreroll);
                _customPreroll = new PrerollOption
                {
                    Seconds = sec,
                    Label = sec > 0 ? sec + " 秒" : "关闭"
                };
                list.Add(_customPreroll);
                hit = _customPreroll;
            }
            else if (_customPreroll != null)
            {
                list.Remove(_customPreroll);   // 又变回标准值了，撤掉占位项
                _customPreroll = null;
            }

            bool old = _suspendEvents;
            _suspendEvents = true;
            try { PrerollCombo.SelectedItem = hit; }
            finally { _suspendEvents = old; }
        }

        private void PrerollCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suspendEvents || _settings == null) return;
            var o = PrerollCombo.SelectedItem as PrerollOption;
            if (o == null) return;

            // 值没变就当没发生：下拉项被容器重建等情况也可能冒事件，
            // 若不拦，配置会被莫名其妙改掉。
            if (o.Seconds == _settings.PrerollSeconds) return;

            ApplyPrerollSeconds(o.Seconds);
        }

        /// <summary>双击下拉框 → 进入输入模式（单击仍然正常展开下拉）</summary>
        private void PrerollCombo_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2) return;
            e.Handled = true;
            BeginPrerollEdit();
        }

        /// <summary>把下拉框换成输入框，填入当前值并全选</summary>
        private void BeginPrerollEdit()
        {
            if (_settings == null) return;
            try { PrerollCombo.IsDropDownOpen = false; } catch { }

            PrerollEdit.Text = Math.Min(Math.Max(0, _settings.PrerollSeconds), PrerollMaxSeconds).ToString();
            PrerollCombo.Visibility = Visibility.Collapsed;
            PrerollEdit.Visibility = Visibility.Visible;
            PrerollEdit.Focus();
            PrerollEdit.SelectAll();
        }

        /// <summary>
        /// 结束输入。apply=true 时提交；打错或 Esc 一律原样退回。
        /// 提交和取消都走这里，保证界面状态一定复原。
        /// </summary>
        private void CommitPrerollEdit(bool apply)
        {
            if (PrerollEdit.Visibility != Visibility.Visible) return;   // 已经退出过了，防重入

            int sec = 0;
            bool ok = apply && TryParsePreroll(PrerollEdit.Text, out sec);

            PrerollEdit.Visibility = Visibility.Collapsed;
            PrerollCombo.Visibility = Visibility.Visible;

            // 打错 / Esc：什么都不改，下拉框显示的还是原来的值
            if (ok) ApplyPrerollSeconds(sec);
        }

        private void PrerollEdit_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; CommitPrerollEdit(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; CommitPrerollEdit(false); }
        }

        /// <summary>点别处 → 也算提交（用户要求：不按回车也生效）</summary>
        private void PrerollEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            CommitPrerollEdit(true);
        }

        /// <summary>重置预录：丢掉已经缓存的那截，从现在重新开始填</summary>
        private void ResetPreroll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_engine == null) { SetStatus("录音引擎未就绪"); return; }
                _engine.ClearPreroll();
                Log.Write("预录缓存已重置");
                SetStatus("预录已重置，从现在重新开始缓存");
            }
            catch (Exception ex)
            {
                Log.Write("重置预录失败：" + ex.Message);
                SetStatus("重置预录失败：" + ex.Message);
            }
        }

        /// <summary>存预录：把当前预录缓存另存成 WAV，落到配置的录音目录</summary>
        private async void SavePreroll_Click(object sender, RoutedEventArgs e)
        {
            if (_savePrerollBusy) return;
            try
            {
                if (_engine == null) { SetStatus("录音引擎未就绪"); return; }

                string dir = _settings.GetRecordingsDir();
                Directory.CreateDirectory(dir);

                string name = "预录_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav";
                string path = Path.Combine(dir, name);

                // 位深跟随「保存格式」；mp3 没有位深概念，用 24 兜底
                int bits = 24;
                var fmt = FormatCombo.SelectedItem as FormatOption;
                if (fmt != null && fmt.Format != "mp3") bits = fmt.Bits;

                // 落盘放到后台线程：预录缓存按 60 秒算是 23MB 左右，按上限 600 秒算是 230MB，
                // 同步写会把 UI 线程冻住好几秒（点下去没反应，用户以为卡死了）。
                // 锁内行为完全不变，音频线程该等的那点时间跟以前一样。
                var engine = _engine;
                string err = null;
                _savePrerollBusy = true;
                if (SavePrerollBtn != null) SavePrerollBtn.IsEnabled = false;
                SetStatus("正在保存预录…");

                bool ok = false;
                try
                {
                    var box = new string[1];
                    ok = await Task.Run(() => engine.SavePrerollToWav(path, bits, out box[0]));
                    err = box[0];
                }
                finally
                {
                    _savePrerollBusy = false;
                    if (SavePrerollBtn != null)
                        SavePrerollBtn.IsEnabled = engine == null || !engine.IsRecording;
                }

                if (ok)
                {
                    Log.Write("预录已保存 -> " + path);
                    // 不自动播放：和正常录音结束一样，只落盘 + 让「试听」按钮能播它
                    _lastPlayablePath = path;
                    // 注意：落盘已经挪到后台线程，这中间用户可能已经点了「开始录音」。
                    // 录音期间「试听」必须保持禁用（否则会切静态波形，把这段录音的实时波形点丢掉），
                    // 所以这里不能无条件置 true，要和 SetRecordingUi 的判定保持一致。
                    if (PlayBtn != null)
                        PlayBtn.IsEnabled = engine == null || !engine.IsRecording;
                    SetStatus("预录已保存：" + Path.GetFileName(path) + "（点「试听」可播放）");
                }
                else
                {
                    Log.Write("保存预录失败：" + err);
                    SetStatus("保存预录失败：" + err);
                }
            }
            catch (Exception ex)
            {
                _savePrerollBusy = false;
                if (SavePrerollBtn != null && _engine != null) SavePrerollBtn.IsEnabled = !_engine.IsRecording;
                Log.Write("保存预录失败：" + ex.Message);
                SetStatus("保存预录失败：" + ex.Message);
            }
        }

        private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suspendEvents) return;
            SaveSettingsFromUi();
        }

        private void RateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suspendEvents) return;
            SaveSettingsFromUi();
        }

        private void PrefixBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suspendEvents) return;
            if (IsLoaded) SaveSettingsFromUi();
        }

        /// <summary>打开任意音频文件直接试听（不需要先录一条）。</summary>
        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要试听的音频文件",
                // 只列 libsndfile 真正能解的格式。
                // 原来还写着 m4a / aac / wma，但解码库不支持，用户按提示选中必然「试听失败」。
                Filter = "音频文件|*.wav;*.mp3;*.aiff;*.aif;*.flac;*.ogg;*.oga;*.caf;*.w64|所有文件|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) == true) PlayFile(dlg.FileName);
        }

        /// <summary>拖文件进来直接播放。</summary>
        private void Window_DragOver(object sender, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (DropHint != null) DropHint.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            if (DropHint != null) DropHint.Visibility = Visibility.Collapsed;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (DropHint != null) DropHint.Visibility = Visibility.Collapsed;

            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            PlayFile(files[0]);
            e.Handled = true;
        }

        /// <summary>
        /// 点窗口任何地方都退出音量输入框。
        /// 只靠 TextBox.LostFocus 不够 —— 有些区域不接受焦点，点了也不会触发失焦。
        /// </summary>
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (RecordVolGauge != null) RecordVolGauge.CommitIfEditing();
            if (PlayVolGauge != null) PlayVolGauge.CommitIfEditing();

            // 预录秒数框同理：只靠 LostKeyboardFocus 不够 ——
            // 点波形、点状态栏这些不接受焦点的区域，点了也不会触发失焦，输入框会一直挂着。
            if (PrerollEdit != null && PrerollEdit.Visibility == Visibility.Visible
                && !PrerollEdit.IsMouseOver)
            {
                CommitPrerollEdit(true);
            }
        }

        private void RecordVol_Changed(object sender, EventArgs e)        {
            if (_engine != null) _engine.InputGain = (float)RecordVolGauge.Value;
            if (_suspendEvents) return;
            if (_settings != null) { _settings.InputGain = RecordVolGauge.Value; ScheduleSave(); }
        }

        private void PlayVol_Changed(object sender, EventArgs e)
        {
            if (_volumeProvider != null) { try { _volumeProvider.Volume = (float)PlayVolGauge.Value; } catch { } }
            if (_suspendEvents) return;
            if (_settings != null) { _settings.PlaybackVolume = PlayVolGauge.Value; ScheduleSave(); }
        }

        // ---------- 音量：数值可直接输入，双击滑块恢复 100% ----------

        // ---------- 设置保存防抖：拖动滑轨时 ValueChanged 每秒会触发几十次，
        //            每次都同步写盘会造成 UI 卡顿，这里合并到停止操作后再写 ----------
        private DispatcherTimer _saveDebounce;

        private void ScheduleSave()
        {
            if (_saveDebounce == null)
            {
                _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _saveDebounce.Tick += (s, e) =>
                {
                    _saveDebounce.Stop();
                    try { SaveSettingsFromUi(); } catch { }
                };
            }
            _saveDebounce.Stop();
            _saveDebounce.Start();
        }

        private void IdleWave_Changed(object sender, RoutedEventArgs e)
        {
            if (_suspendEvents || _settings == null) return;
            _settings.HideWaveformIdle = IdleWaveCheck.IsChecked != true;
            _settings.Save();
            if (_settings.HideWaveformIdle && !_engine.IsRecording) Waveform.Clear();
        }

        /// <summary>波形刻度按钮：点击在 线性 ⇄ 对数(dB) 之间切换，按钮上直接显示当前模式</summary>
        private void ScaleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            bool log = !_settings.WaveLogScale;
            _settings.WaveLogScale = log;
            _settings.Save();
            if (Waveform != null) Waveform.UseLogScale = log;
            UpdateScaleBtn();
            Log.Write("波形刻度切换为 " + (log ? "LOG(dB)" : "LIN"));
        }

        private void UpdateScaleBtn()
        {
            if (ScaleBtn == null) return;
            bool log = _settings != null && _settings.WaveLogScale;
            ScaleBtn.Content = log ? "刻度：LOG(dB)" : "刻度：线性";
        }

        // ==================== 录音 ====================

        private void RecordBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_processing) return;
            if (_engine.IsRecording) StopAndProcess(includePreroll: true);
            else StartRecording();
        }

        /// <summary>「丢弃」：停止录音、删掉临时文件，这次录到的内容不保存</summary>
        private void RecordBtnDiscard_Click(object sender, RoutedEventArgs e)
        {
            if (_processing) return;
            if (!_engine.IsRecording)
            {
                // 设备被拔掉/中断后界面没跟上时，点一下把界面拉回待机
                SetRecordingUi(false);
                return;
            }

            long frames = StopRecordingInternal(silent: false);
            var temp = _tempWav;
            _tempWav = null;

            if (!string.IsNullOrEmpty(temp)) TryDelete(temp);
            if (Waveform != null) Waveform.Clear();   // 内容已经不要了，画面上也别留着

            Log.Write(string.Format("丢弃录音: {0} 帧，临时文件已删除: {1}", frames, temp ?? "null"));
            SetStatus("已丢弃本次录音（未保存）");
        }

        /// <summary>「停止（不含预录）」：停下来，并把文件开头那段预录裁掉再保存</summary>
        private void RecordBtnNoPreroll_Click(object sender, RoutedEventArgs e)
        {
            if (_processing) return;
            if (_engine.IsRecording) { StopAndProcess(includePreroll: false); return; }

            // 设备被拔掉/中断时引擎会自己把录音状态复位，但界面收不到通知，
            // 于是两个停止按钮会留在屏幕上。点一下就把界面拉回待机，不留死按钮。
            SetRecordingUi(false);
        }

        private void StartRecording()
        {
            var src = CurrentSource;
            if (src == null || !_engine.IsDeviceOpen)
            {
                Log.Write("开始录音被拒: 设备未就绪");
                SetStatus("设备未就绪，无法录音");
                return;
            }

            try
            {
                var fmt = FormatCombo.SelectedItem as FormatOption ?? new FormatOption { Format = "wav", Bits = 32 };
                SaveSettingsFromUi();
                // 这条录音从零开始计「有没有爆过」，
                // 否则待机监视阶段亮起的灯会被误读成「这一条也爆了」
                if (InClipLight != null) InClipLight.Reset();
                if (OutClipLight != null) OutClipLight.Reset();

                var dir = ResolveRecordingsDir();
                Directory.CreateDirectory(dir);
                _tempDir = dir;

                _tempWav = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".tmp.wav");

                // 开头一律先把预录接进去：停止那一刻才有得选。
                // 「要不要预录」由两个停止按钮决定（见 StopAndProcess）。
                Log.Write(string.Format("开始录音 -> {0} | 格式={1} | 增益={2:0.00}",
                    _tempWav, fmt.Label, _engine.InputGain));

                _engine.StartRecording(_tempWav, "wav", fmt.Bits, fmt.Bitrate, 0, includePreroll: true);

                // 只有在真的试听中才停止播放；否则不要碰波形，保持连续显示
                if (_player != null) StopPlayback();

                _recordStart = DateTime.Now;
                _pausedTotal = TimeSpan.Zero;
                _frozenElapsed = TimeSpan.Zero;
                SetRecordingUi(true);
                SetStatus("● 正在录音…");
            }
            catch (Exception ex)
            {
                Log.Error("StartRecording", ex);
                SetStatus("开始录音失败：" + ex.Message);
            }
        }

        private void PauseBtn_Click(object sender, RoutedEventArgs e)
        {
            // 播放中：这个按钮变成试听的暂停 / 继续（录音和试听共用同一个按钮）
            if (_player != null)
            {
                try
                {
                    if (_player.PlaybackState == PlaybackState.Playing)
                    {
                        _player.Pause();
                        PauseBtnText.Text = "▶ 继续";
                        PauseBtn.Background = (System.Windows.Media.Brush)FindResource("Warn");
                        SetStatus("⏸ 试听已暂停");
                    }
                    else
                    {
                        _player.Play();
                        PauseBtnText.Text = "⏸ 暂停";
                        PauseBtn.Background = (System.Windows.Media.Brush)FindResource("CardBg");
                        SetStatus("▶ 继续试听…");
                    }
                }
                catch (Exception ex) { Log.Error("PausePlayback", ex); }
                return;
            }

            if (!_engine.IsRecording) return;
            try
            {
                if (_engine.IsPaused)
                {
                    _engine.ResumeRecording();
                    _pausedTotal += DateTime.Now - _pausedAt;
                    Waveform.SetPaused(false);   // 恢复平滑推进
                    PauseBtnText.Text = "⏸ 暂停";
                    PauseBtn.Background = (System.Windows.Media.Brush)FindResource("CardBg");
                    SetStatus("● 继续录音…");
                }
                else
                {
                    _engine.PauseRecording();
                    _pausedAt = DateTime.Now;
                    _frozenElapsed = _pausedAt - _recordStart - _pausedTotal;
                    // 波形也要立刻停住：不然它还会追平滑存货那一段，
                    // 看起来就是「时间轴已经停了、波形还在往前滚」
                    Waveform.SetPaused(true);
                    PauseBtnText.Text = "▶ 继续";
                    PauseBtn.Background = (System.Windows.Media.Brush)FindResource("Warn");
                    SetStatus("⏸ 已暂停（这段不会写入文件）");
                }
            }
            catch (Exception ex) { Log.Error("Pause", ex); }
        }

        /// <summary>
        /// 停止录音并按用户的选择保存：选「不含预录」时，先把文件开头那段预录裁掉，
        /// 只留下从「点录音」那一刻开始的音频。
        /// </summary>
        /// <param name="includePreroll">false = 把文件开头那段预录裁掉再保存</param>
        private void StopAndProcess(bool includePreroll)
        {
            var fmt = FormatCombo.SelectedItem as FormatOption ?? new FormatOption { Format = "wav", Bits = 32 };
            var rateOpt = RateCombo.SelectedItem as RateOption;
            int targetRate = rateOpt == null ? 0 : rateOpt.Rate;

            long frames = StopRecordingInternal(silent: false);
            // 停止后引擎仍保留「开头写了多少帧预录」——这里取快照，后面裁剪用
            long prerollFrames = includePreroll ? 0 : Math.Max(0, _engine.PrerollFramesInFile);

            var temp = _tempWav;
            _tempWav = null;

            if (temp == null || !File.Exists(temp))
            {
                Log.Write("停止录音后没有临时文件: frames=" + frames);
                SetStatus("未录到任何音频");
                return;
            }

            long size = 0;
            try { size = new FileInfo(temp).Length; } catch { }
            Log.Write(string.Format("停止录音: frames={0} 临时文件={1} bytes 需裁预录={2} 帧",
                frames, size, prerollFrames));

            // 引擎一帧都没写进来 = 真的没录到内容。
            // 不用「文件 ≤ 44 字节」这种启发式：32 位浮点 WAV 的头可能更长（带 fact 块），
            // 零帧文件能骗过 44 字节判定，最后存出一个只有头的假文件。
            if (frames <= 0) { TryDelete(temp); SetStatus("未录到任何音频"); return; }

            string finalPath;
            try { finalPath = DetermineFinalPath(fmt); }
            catch (Exception ex) { Log.Error("DetermineFinalPath", ex); TryDelete(temp); SetStatus("保存失败：" + ex.Message); return; }
            if (finalPath == null) { Log.Write("用户取消了保存"); TryDelete(temp); SetStatus("已取消保存"); return; }

            Log.Write("最终文件: " + finalPath);

            bool needMp3 = fmt.Format == "mp3";
            bool needResample = targetRate > 0 && targetRate != _engine.SampleRate;
            int bitrate = fmt.Bitrate > 0 ? fmt.Bitrate : 192;
            int srcRate = _engine.SampleRate;

            // 快速路径：不裁预录、不转码、不重采样 —— 和以前完全一样，直接改名
            if (prerollFrames <= 0 && !needMp3 && !needResample)
            {
                var res = AudioSaver.Save(temp, finalPath, false, bitrate, 0, srcRate);
                if (res.Ok) { Log.Write("已保存: " + res.FinalPath); FinishSaved(res.FinalPath, frames, null); }
                else { Log.Write("保存失败: " + res.Error); TryDelete(temp); SetStatus("保存失败：" + res.Error); }
                return;
            }

            _processing = true;
            RecordBtn.IsEnabled = false;
            RecordBtnNoPreroll.IsEnabled = false;
            RecordBtnDiscard.IsEnabled = false;
            SetStatus(prerollFrames > 0 ? "正在去掉开头的预录…" : (needMp3 ? "正在编码 MP3…" : "正在转换采样率…"));

            Task.Run(() =>
            {
                string workTemp = temp;
                long keptFrames = frames;
                string note = null;
                SaveResult res = null;

                try
                {
                    if (prerollFrames > 0)
                    {
                        // 临时名必须仍以 .tmp.wav 结尾：CleanupStaleTemp 只扫这个模式，
                        // 否则强杀进程后裁剪残片会永久留在用户的录音目录里。
                        string trimmed = temp + ".trim.tmp.wav";
                        string err;
                        if (AudioSaver.TrimLeadingFrames(temp, trimmed, prerollFrames, out err))
                        {
                            TryDelete(temp);
                            workTemp = trimmed;
                            keptFrames = Math.Max(0, frames - prerollFrames);
                        }
                        else
                        {
                            // 裁不掉就退回「含预录保存」：绝不丢录音，也不静默糊过去
                            TryDelete(trimmed);
                            note = "未能去掉预录（" + err + "），已按含预录保存";
                            Log.Write(note);
                        }
                    }

                    res = AudioSaver.Save(workTemp, finalPath, needMp3, bitrate, targetRate, srcRate);
                }
                catch (Exception ex)
                {
                    // 下游各自都吞了异常，这里再兜一层：以后有人在 Task 里加了会抛的调用，
                    // 也必须保证界面复位，否则 _processing 永久 true、两个录音按钮全废。
                    Log.Error("后处理异常", ex);
                    res = new SaveResult { Error = ex.GetType().Name + ": " + ex.Message };
                }
                finally
                {
                    var doneRes = res;
                    var doneTemp = workTemp;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _processing = false;
                        RecordBtn.IsEnabled = true;
                        RecordBtnNoPreroll.IsEnabled = _engine.IsRecording;   // 未录音时它本来就是收起的
                        RecordBtnDiscard.IsEnabled = _engine.IsRecording;

                        if (doneRes != null && doneRes.Ok)
                        {
                            Log.Write("后处理完成: " + doneRes.FinalPath);
                            FinishSaved(doneRes.FinalPath, keptFrames, note);
                            return;
                        }

                        string err = doneRes == null ? "未知错误" : doneRes.Error;
                        Log.Write("后处理失败: " + err);
                        var keep = Path.ChangeExtension(finalPath, ".wav");
                        bool moved = false;
                        try
                        {
                            if (File.Exists(keep)) keep = UniquePath(keep);
                            if (File.Exists(doneTemp)) { File.Move(doneTemp, keep); moved = true; }
                        }
                        catch (Exception ex2) { Log.Error("保留WAV失败", ex2); }

                        // 只有真的移过去了才能说「已保留 WAV」，否则要把原始位置告诉用户
                        string tail;
                        if (moved) { tail = "（已保留 WAV）"; _lastPlayablePath = keep; PlayBtn.IsEnabled = true; }
                        else if (File.Exists(doneTemp)) tail = "（原始文件仍在：" + doneTemp + "）";
                        else tail = "";
                        SetStatus((note == null ? "" : note + "；") + "转换失败：" + err + tail);
                    }));
                }
            });
        }

        private long StopRecordingInternal(bool silent)
        {
            long frames = 0;
            try { frames = _engine.StopRecording(); }
            catch (Exception ex) { if (!silent) SetStatus("停止录音异常：" + ex.Message); }
            SetRecordingUi(false);
            return frames;
        }

        private void SetRecordingUi(bool recording)
        {
            RecordBtn.Background = recording
                ? (System.Windows.Media.Brush)FindResource("Accent")
                : (System.Windows.Media.Brush)FindResource("Danger");
            RecordBtnText.Text = recording ? "■ 停止" : "● 开始录音";
            // 这两个按钮只在录音时出现；未录音时收起、不占位
            if (RecordBtnNoPreroll != null)
            {
                RecordBtnNoPreroll.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
                RecordBtnNoPreroll.IsEnabled = recording;
            }
            if (RecordBtnDiscard != null)
            {
                RecordBtnDiscard.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
                RecordBtnDiscard.IsEnabled = recording;
            }
            // 录音中、或正在试听时都可用（同一个按钮兼管两边）
            PauseBtn.IsEnabled = recording || _player != null;

            // 录音中禁用「存预录」：这时候数据直接写进文件、不进缓存，
            // 点它只会得到「缓存里还没有数据」，不如果断禁掉
            if (SavePrerollBtn != null) SavePrerollBtn.IsEnabled = !recording;

            // 录音结束时确保波形解除暂停态（暂停中直接停止录音时必须复位）
            if (!recording && Waveform != null) Waveform.SetPaused(false);

            // 待机不显示波形时，把这次录音留下的波形清掉。
            // 原来 UiTimer 里只有一个空的 `if (HideWaveformIdle && pts.Length > 0) { }`，
            // 注释写着「清掉已有内容」但代码根本没写 —— 于是关掉「待机也显示波形」之后，
            // 停止录音仍会把上一次的波形留在屏幕上。
            if (!recording && _settings != null && _settings.HideWaveformIdle && Waveform != null)
                Waveform.Clear();

            // 录音中禁用「录制通道」下拉框：换设备会导致采样率/声道可能不一致，
            // 同一份 WAV 里混两种格式就废了，直接从源头断掉这个操作
            if (CaptureCombo != null) CaptureCombo.IsEnabled = !recording;

            // 录音中禁用「试听」：一边录一边放会把暂停键抢走（同一个按钮兼管两边），
            // 而且试听会切到静态波形模式，那段时间的实时波形点会被直接丢掉。
            if (PlayBtn != null)
            {
                if (recording) PlayBtn.IsEnabled = false;
                else if (!string.IsNullOrEmpty(_lastPlayablePath) && File.Exists(_lastPlayablePath))
                    PlayBtn.IsEnabled = true;
            }

            // 只把 IsEnabled 设成 false 在界面上看不出来 —— 暗色主题的 ComboBox 模板
            // 没写禁用态样式。所以把整块（标签 + 下拉框 + 格式说明）一起调暗。
            if (CapturePanel != null) CapturePanel.Opacity = recording ? 0.4 : 1.0;
            if (!recording && _player == null)
            {
                PauseBtnText.Text = "⏸ 暂停";
                PauseBtn.Background = (System.Windows.Media.Brush)FindResource("CardBg");
                TimerText.Text = "00:00:000";
            }
            else TimerText.Text = "00:00:000";

            // 缩在托盘里时，悬浮提示也能看出是不是正在录
            if (_tray != null) _tray.SetText(recording ? "RecLite 轻录音 — 正在录音" : "RecLite 轻录音");
        }

        private void FinishSaved(string path, long frames, string note)
        {
            var secs = _engine.SampleRate > 0 ? frames / (double)_engine.SampleRate : 0;
            _lastPlayablePath = path;      // 录完的文件也能「再点试听」重播
            PlayBtn.IsEnabled = true;
            string msg = string.Format("✓ 已保存（{0:0.0} 秒）：{1}", secs, path);
            if (!string.IsNullOrEmpty(note)) msg = "⚠ " + note + " — " + msg;
            SetStatus(msg);
        }

        // ==================== 试听 ====================

        private void PlayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_player != null)
            {
                // 主动点停止：自然播完那条路径由 UiTimer 轮询 PlaybackState 收尾并提示，
                // 手动停在这是另一条路径，必须自己补一句状态提示，否则界面什么都不说
                StopPlayback();
                SetStatus("试听结束");
                return;
            }

            // 文件可能被外部删掉/改名了：给出明确提示并禁用按钮，而不是点了没反应
            if (string.IsNullOrEmpty(_lastPlayablePath) || !File.Exists(_lastPlayablePath))
            {
                SetStatus("文件已不存在：" + (string.IsNullOrEmpty(_lastPlayablePath) ? "（还没录过也没打开过文件）" : _lastPlayablePath));
                if (!string.IsNullOrEmpty(_lastPlayablePath)) PlayBtn.IsEnabled = false;
                return;
            }
            PlayFile(_lastPlayablePath);
        }

        /// <summary>mm:ss:mmm（分钟不封顶，超过 99 分钟自然变三位）</summary>
        private static string FormatMs(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return string.Format("{0:00}:{1:00}:{2:000}",
                (int)t.TotalMinutes, t.Seconds, t.Milliseconds);
        }

        private bool PlayFile(string path)
        {
            var token = ++_playToken;
            _lastPlayablePath = path;
            try
            {
                StopPlayback();
                var dev = PlaybackCombo.SelectedItem as AudioDeviceInfo;
                var mmdev = dev != null ? DeviceService.FindById(dev.Id) : null;

                // 统一走 libsndfile：WAV / AIFF / FLAC / OGG / MP3 / CAF / W64 / MAT
                // 不再使用 MediaFoundationReader —— 它的 FLAC 随机定位会让解码器断流，
                // 进而导致 Stop() 死等、整个程序卡死（详见 SndFileReader 的注释）。
                var snd = new SndFileReader(path);
                WaveStream stream = snd;

                _reader = stream;
                _volumeProvider = new VolumeSampleProvider(stream.ToSampleProvider())
                {
                    Volume = (float)PlayVolGauge.Value
                };

                _player = mmdev != null
                    // 缓冲从 200ms 压到 60ms：seek 时已经提交给系统的旧数据会先播完再接新位置，
                    // 缓冲越大「重复一小段」越明显。60ms 在文件播放场景下足够稳。
                    ? (IWavePlayer)new WasapiOut(mmdev, AudioClientShareMode.Shared, false, 60)
                    : new WaveOutEvent();

                // 不在这个事件里做停止逻辑！
                // 内部 seek 用的 _player.Stop() 也会触发它，而它是 Dispatcher.BeginInvoke 异步派发的，
                // 等真正执行时 _seekInProgress 已经被清掉了 —— 分辨不出来，会把 seek 当成「播完」关掉播放。
                // 播完检测交给 UiTimer 里轮询 PlaybackState。
                _player.PlaybackStopped += (s, a) =>
                    Log.Write(string.Format("[播放] PlaybackStopped（异常={0}）",
                        a.Exception == null ? "无" : a.Exception.Message));

                // 输出电平表要看到「文件实际输出」的电平，所以包一层只读取样的中继
                _previewMeter = new MeteringProvider(_volumeProvider, stream);
                _player.Init(_previewMeter);

                // 试听时输出表的 0 dB 按文件自己的位深来（拿不到就按 32 float）
                int srcBits = snd.SourceBits > 0 ? snd.SourceBits : 32;
                _outFullScale = ClipLevel.FullScale(srcBits);
                _previewInfo = string.Format("{0} Hz · {1} 声道 · {2}",
                    snd.WaveFormat.SampleRate, snd.WaveFormat.Channels, snd.SourceDescription);
                if (OutMeter != null) OutMeter.SetFullScale(_outFullScale);
                // 波形也按文件自己的满幅判削波；阈值抬高 1e-6 的用意：
                // 商业母带的峰值被限幅器压在「正好 0 dBFS」，等于满幅而非超过，
                // 抬高一点点就能让这些正常峰值不标红，而真正越界的仍然红。
                if (Waveform != null) Waveform.SetClipThreshold(_outFullScale * (1.0 + 1e-6));
                if (OutClipLight != null) OutClipLight.Reset();

                _player.Play();
                _playSmoothPos = 0;
                ReanchorPlayhead();
                PlayBtn.IsEnabled = true;      // XAML 里初始是禁用的，开始播必须打开，否则「停止」点不了
                PlayBtnText.Text = "■ 停止";
                // 播放期间暂停按钮接管「暂停试听」
                PauseBtn.IsEnabled = true;
                PauseBtnText.Text = "⏸ 暂停";
                PauseBtn.Background = (System.Windows.Media.Brush)FindResource("CardBg");
                SetStatus("▶ 正在试听：" + Path.GetFileName(path));

                // 异步生成文件波形，切换到「静态波形」模式显示
                var p = path;
                Task.Run(() =>
                {
                    int ch;
                    var pts = WaveformLoader.Load(p, 6000, out ch);
                    Log.Write(string.Format("文件波形加载: {0} 点, 声道={1}, 文件={2}",
                        pts.Length, ch, Path.GetFileName(p)));
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_playToken == token && _player != null)
                        {
                            Waveform.SetStatic(pts, ch);
                            Waveform.StaticDurationSeconds = _reader != null ? _reader.TotalTime.TotalSeconds : 0;
                            Waveform.SetPlayProgress(0);
                        }
                    }));
                });

                return true;
            }
            catch (Exception ex)
            {
                Log.Error("PlayFile", ex);
                StopPlayback();
                SetStatus("试听失败：" + ex.Message);
                return false;
            }
        }

        private void StopPlayback()
        {
            try { _player?.Stop(); } catch (Exception ex) { Log.Error("StopPlayback.Stop", ex); }
            try { _player?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            _player = null;
            _reader = null;
            _previewMeter = null;          // 取样中继失效后输出表回落 0
            _previewInfo = "";
            _volumeProvider = null;

            // 试听结束后暂停按钮交还给录音
            if (PauseBtn != null)
            {
                PauseBtn.IsEnabled = _engine != null && _engine.IsRecording;
                PauseBtnText.Text = "⏸ 暂停";
                PauseBtn.Background = (System.Windows.Media.Brush)FindResource("CardBg");
            }
            if (PlayBtnText != null) PlayBtnText.Text = "▶ 试听";
            // 退出静态波形，恢复实时显示
            try { Waveform.ClearStatic(); } catch { }
            // 输出表的 0 dB 从「文件位深」切回「保存格式」
            RefreshClipScale();
        }

        // ==================== 文件列表 ====================

        // ==================== 打开录音目录（多级回退 + 真实错误码）====================

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        private void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            var dir = ResolveRecordingsDir();
            try { Directory.CreateDirectory(dir); } catch { }

            string e1, e2;

            // 方式 1：Shell API —— 纯 API 调用，不创建进程，不会触发 Windows 自己的错误对话框
            // （实测在你的机器上返回 0，窗口能正常打开）
            if (TryShellApi(dir, out e1)) { SetStatus("已打开文件夹：" + dir); Log.Write("OpenDir[1] SHOpenFolderAndSelectItems 成功：" + dir); return; }
            Log.Write("OpenDir[1] SHOpenFolderAndSelectItems 失败: " + e1);

            // 方式 2：Shell COM 的 Explore
            if (TryShellComExplore(dir, out e2)) { SetStatus("已打开文件夹：" + dir); Log.Write("OpenDir[2] Shell.Application.Explore 成功"); return; }
            Log.Write("OpenDir[2] Shell.Application.Explore 失败: " + e2);

            // 说明：不再尝试 explorer.exe / ShellExecute。
            // 它们失败时 Windows 会自己弹「无法访问指定设备、路径或文件」对话框，程序内拦不住。

            // 方式 4：路径复制到剪贴板
            try { Clipboard.SetText(dir); } catch { }
            MessageBox.Show(
                "系统拒绝打开文件夹（三种方式都失败）。\n\n" +
                "路径已复制到剪贴板，可以粘贴到资源管理器地址栏：\n" + dir,
                "RecLite", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>更改录音保存目录（弹系统选目录窗口）</summary>
        private void ChangeDir_Click(object sender, RoutedEventArgs e)
        {
            if (_engine != null && _engine.IsRecording)
            {
                MessageBox.Show("正在录音中，请先停止录音再更改保存目录。", "RecLite",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string current = ResolveRecordingsDir();
            try
            {
                using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dlg.Description = "选择录音保存目录";
                    dlg.ShowNewFolderButton = true;
                    if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
                        dlg.SelectedPath = current;

                    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                    string picked = dlg.SelectedPath;
                    if (string.IsNullOrWhiteSpace(picked)) return;

                    try
                    {
                        Directory.CreateDirectory(picked);
                    }
                    catch (Exception exDir)
                    {
                        // 目录建不出来（只读介质 / 无权限 / 断开的网络盘）就别装成功，
                        // 否则用户要等到真正录音那一刻才发现写不进去
                        MessageBox.Show("这个目录写不进去，没有切换：\n" + picked + "\n\n" + exDir.Message,
                            "RecLite", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    _settings.RecordingsDir = picked;
                    _settings.Save();
                    DirBox.Text = _settings.GetRecordingsDir();
                    SetStatus("录音目录已改为：" + picked);
                    Log.Write("录音目录改为：" + picked);
                }
            }
            catch (Exception ex)
            {
                Log.Error("ChangeDir_Click", ex);
                MessageBox.Show("更改目录失败：\n" + ex.Message, "RecLite",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>用 Shell COM 的 Explore 让已运行的资源管理器打开指定文件夹</summary>
        private bool TryShellComExplore(string dir, out string err)
        {
            err = null;
            object shell = null;
            try
            {
                var t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) { err = "Shell.Application 未注册"; return false; }

                shell = Activator.CreateInstance(t);
                if (shell == null) { err = "创建 Shell.Application 失败"; return false; }

                t.InvokeMember("Explore", System.Reflection.BindingFlags.InvokeMethod,
                    null, shell, new object[] { dir });
                return true;
            }
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; return false; }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell))
                {
                    try { Marshal.ReleaseComObject(shell); } catch { }
                }
            }
        }

        private bool TryShellApi(string dir, out string err)
        {
            err = null;
            IntPtr pidl = IntPtr.Zero;
            try
            {
                uint attrs;
                int hr = SHParseDisplayName(dir, IntPtr.Zero, out pidl, 0, out attrs);
                if (hr != 0 || pidl == IntPtr.Zero)
                {
                    err = "SHParseDisplayName hr=0x" + hr.ToString("X8");
                    return false;
                }

                hr = SHOpenFolderAndSelectItems(pidl, 0, null, 0);
                if (hr != 0)
                {
                    err = "SHOpenFolderAndSelectItems hr=0x" + hr.ToString("X8");
                    return false;
                }
                return true;
            }
            catch (Exception ex) { err = ex.Message; return false; }
            finally { if (pidl != IntPtr.Zero) { try { ILFree(pidl); } catch { } } }
        }

        // ==================== 文件命名 ====================

        private string ResolveRecordingsDir() => AppPaths.Resolve(_settings.GetRecordingsDir());

        private string BuildBaseName()
        {
            var prefix = string.IsNullOrWhiteSpace(PrefixBox.Text) ? "录音" : PrefixBox.Text.Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) prefix = prefix.Replace(c, '_');
            return string.Format("{0}_{1:yyyyMMdd_HHmmss}", prefix, DateTime.Now);
        }

        private string DetermineFinalPath(FormatOption fmt)
        {
            var dir = _tempDir ?? ResolveRecordingsDir();
            Directory.CreateDirectory(dir);

            string baseName;
            if (_settings.FileNameMode == "ask")
            {
                var dlg = new InputDialog("保存录音", BuildBaseName(), "保存到：" + dir) { Owner = this };
                if (dlg.ShowDialog() == true) baseName = dlg.Value;
                else return null;
            }
            else baseName = BuildBaseName();

            var ext = fmt.Format == "mp3" ? ".mp3" : ".wav";
            return UniquePath(Path.Combine(dir, baseName + ext));
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int i = 2; i < 1000; i++)
            {
                var p = Path.Combine(dir, string.Format("{0}_{1}{2}", name, i, ext));
                if (!File.Exists(p)) return p;
            }
            return path;
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>把界面上的所有设置同步到配置对象并落盘</summary>
        private void SaveSettingsFromUi()
        {
            if (_settings == null) return;

            // 预录不走这里：ApplyPrerollSeconds 改的时候就已经落盘了。
            // 别再从 PrerollCombo.Text 反解析一遍 —— 非编辑态 ComboBox 的 Text 不可靠，
            // 万一读出个错值会把用户的设置改坏。

            var rate = RateCombo.SelectedItem as RateOption;
            if (rate != null) _settings.SampleRate = rate.Rate;

            var fmt = FormatCombo.SelectedItem as FormatOption;
            if (fmt != null)
            {
                _settings.OutputFormat = fmt.Format;
                if (fmt.Format == "mp3") _settings.Mp3Bitrate = fmt.Bitrate;
                else _settings.BitsPerSample = fmt.Bits;
            }

            // 位深一变，削波线就跟着变 —— 红色要落在新格式自己的 0 dBFS 上
            RefreshClipScale();

            // 总是记住当前选中的设备（用户要求：下次打开保持上次的设备，不要回到系统默认）
            var curCap = CaptureCombo.SelectedItem as AudioDeviceInfo;
            if (curCap != null) _settings.CaptureDeviceId = curCap.Id;

            var curPlay = PlaybackCombo.SelectedItem as AudioDeviceInfo;
            if (curPlay != null) _settings.PlaybackDeviceId = curPlay.Id;

            // 窗口位置与大小
            try
            {
                var b = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, Width, Height)
                    : RestoreBounds;
                if (b.Width > 100 && b.Height > 100)
                {
                    _settings.WindowLeft = b.Left;
                    _settings.WindowTop = b.Top;
                    _settings.WindowWidth = b.Width;
                    _settings.WindowHeight = b.Height;
                }
            }
            catch { }

            _settings.FileNameMode = NameModeCombo.SelectedIndex == 1 ? "ask" : "auto";
            _settings.FileNamePrefix = string.IsNullOrWhiteSpace(PrefixBox.Text) ? "录音" : PrefixBox.Text.Trim();
            _settings.InputGain = RecordVolGauge.Value;
            _settings.PlaybackVolume = PlayVolGauge.Value;
            _settings.HideWaveformIdle = IdleWaveCheck.IsChecked != true;

            _settings.Save();
        }

        // ==================== UI 刷新 ====================

        private long _lastUiErrTicks;              // 上次记录 UiTimer 异常的时间戳（节流用）

        /// <summary>输入侧满幅（设备能力）—— 输入电平表的 0 dB</summary>
        private double _inFullScale = 32767.0 / 32768.0;

        /// <summary>输出侧满幅（保存格式 / 试听文件位深）—— 输出电平表的 0 dB</summary>
        private double _outFullScale = 32767.0 / 32768.0;

        /// <summary>波形判定用的满幅：设备与保存两侧取更严的那条</summary>
        private double _clipFullScale = 32767.0 / 32768.0;

        /// <summary>试听时的电平取样中继</summary>
        private MeteringProvider _previewMeter;

        /// <summary>试听文件的格式描述（采样率 / 声道 / 位深），显示在计时器下面</summary>
        private string _previewInfo = "";

        // ---------- 播放头平滑 ----------
        // 不能直接用 _reader.CurrentTime：那是「已读了多少字节」，被输出缓冲提前拉走，
        // 而且按块跳变（每 ~200 ms 跳一次），所以黄条会一顿一顿。
        // 这里用 Stopwatch 平滑推进，再每秒用真实位置校正一次漂移。
        private long _playAnchorTicks;     // 锚点时刻
        private double _playAnchorPos;     // 锚点位置（秒）
        private double _playSmoothPos;     // 平滑后的当前播放位置（秒）
        private long _lastCorrectTicks;    // 上次校正时刻

        private void ReanchorPlayhead()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            _playAnchorTicks = now;
            _playAnchorPos = _playSmoothPos;
            _lastCorrectTicks = now;
        }

        /// <summary>每帧推进平滑位置；返回 0~1 的相对进度。</summary>
        private double AdvancePlayhead()
        {
            if (_reader == null) return 0;

            double total;
            try { total = _reader.TotalTime.TotalSeconds; } catch { return 0; }
            if (total <= 0.01) return 0;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long freq = System.Diagnostics.Stopwatch.Frequency;

            // 只有真的在播时才推进；暂停时冻结
            bool playing = _player != null && _player.PlaybackState == PlaybackState.Playing;
            if (playing)
            {
                double elapsed = (now - _playAnchorTicks) / (double)freq;
                _playSmoothPos = _playAnchorPos + elapsed;
            }

            // 不用 _reader.CurrentTime 校正 —— MediaFoundationReader（FLAC/M4A 走这条）
            // 每次读它都要跟解码器同步，实测会让 UI 从 200fps 掉到 123fps 直至卡死。
            // Stopwatch 的漂移在几分钟内可以忽略，seek 时重新锚定就够准了。

            double p = _playSmoothPos / total;
            if (p < 0) p = 0;
            if (p > 1) p = 1;
            return p;
        }

        /// <summary>
        /// 重新计算并下发三套满幅值：
        ///   输入表 = 设备能力；输出表 = 保存格式；波形 = 两者取更严。
        /// 采集侧把样本除以「采集位深满幅」归一化到 ±1.0，所以 16/24 bit 整数采集
        /// 永远到不了 1.0 —— 波形若只按保存格式判，就会出现「设备已经爆了、波上却不到 0 dB」。
        /// </summary>
        private void RefreshClipScale()
        {
            if (_settings == null || _engine == null) return;

            int capBits = _engine.BitsPerSample > 0 ? _engine.BitsPerSample : 32;
            int outBits = _settings.BitsPerSample > 0 ? _settings.BitsPerSample : capBits;

            _inFullScale = ClipLevel.FullScale(capBits);
            _outFullScale = ClipLevel.FullScale(outBits);
            _clipFullScale = Math.Min(_inFullScale, _outFullScale);

            // 保存设置在控件就绪之前也可能被调用（例如构造阶段），那时只更新数值
            if (InMeter != null) InMeter.SetFullScale(_inFullScale);
            if (OutMeter != null) OutMeter.SetFullScale(_outFullScale);

            // 试听中不要动波形的削波阈值：播放一律按文件自己的满幅算，
            // 不能被「保存格式」的位深覆盖掉
            if (Waveform != null && _reader == null) Waveform.SetClipThreshold(_clipFullScale);
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            // 时间轴跟着波形走（刻度本身很轻，每帧重画开销可忽略）
            try
            {
                if (Timeline != null)
                {
                    // 0 秒 = 预录起点：预录开着时数据时长 = 预录 + 已录；
                    // 待机且没开预录 → null，时间轴保持静态刻度不乱爬。
                    double pre = _engine != null ? _engine.PrerollSeconds : 0;
                    double? cur = null;

                    if (_engine != null && _engine.IsRecording)
                    {
                        var el2 = _engine.IsPaused
                            ? _frozenElapsed
                            : (DateTime.Now - _recordStart - _pausedTotal);
                        cur = pre + el2.TotalSeconds;
                    }
                    else if (pre > 0.2)
                    {
                        cur = pre;
                    }

                    Timeline.SetTime(cur, pre);
                    Timeline.InvalidateVisual();
                }
            }
            catch { }
            try { if (DbScale != null) DbScale.InvalidateVisual(); } catch { }

            try
            {
                bool recording = _engine.IsRecording;

                bool showWave = recording || !_settings.HideWaveformIdle;

                var pts = _engine.Waveform.Drain();   // 无论显不显示都要取走，避免积压
                if (showWave && pts.Length > 0) Waveform.Append(pts);

                var peak = _engine.Waveform.CurrentPeak(_engine.Channels);

                // 输入表只管设备这一侧：设备扛不住就红，与保存成什么格式无关
                if (InMeter != null)
                {
                    InMeter.SetLevels(peak);
                    if (InClipLight != null)
                    {
                        for (int i = 0; i < peak.Length && i < 2; i++)
                            if (peak[i] >= _inFullScale) InClipLight.SetClipped(i);
                    }
                }

                // 输出表管文件这一侧：录音时是「将要写进文件的值」，
                // 试听时是「文件播放出来的值」，各自按自己的位深判爆
                if (OutMeter != null)
                {
                    if (recording)
                    {
                        OutMeter.SetLevels(peak);
                        if (OutClipLight != null)
                        {
                            for (int i = 0; i < peak.Length && i < 2; i++)
                                if (peak[i] >= _outFullScale) OutClipLight.SetClipped(i);
                        }
                    }
                    else if (_previewMeter != null)
                    {
                        float pl = _previewMeter.Peak;
                        OutMeter.SetLevel(pl);
                        if (pl >= _outFullScale && OutClipLight != null) OutClipLight.SetClipped();
                    }
                    else
                    {
                        OutMeter.SetLevel(0f);
                    }
                }

                // ===== 秒数指示器 =====
                // 它表示「时间轴上的当前位置」，三段颜色和时间轴完全一致（都用 WavePalette）：
                //   白  00:00:000  什么都没在跑
                //   绿  预录缓存中，跟着缓存量读秒；缓存满了就停在设置的秒数上，
                //       改预录秒数它也跟着变
                //   黄  正式录音中，从预录那一段接着往后数（预录关着就直接从 0 起变黄）
                if (recording)
                {
                    // 录制中 PrerollSeconds 返回的是「开始那一刻冻结的快照」——
                    // 缓存此刻已经倒空，只能靠快照才知道该从几秒接着数
                    double preNow = _engine.PrerollSeconds;
                    var el = _engine.IsPaused
                        ? _frozenElapsed
                        : (DateTime.Now - _recordStart - _pausedTotal);

                    TimerText.Text = FormatMs(TimeSpan.FromSeconds(preNow) + el);
                    TimerText.Foreground = WavePalette.RecordingText;
                    PrerollText.Text = "";
                }
                else
                {
                    var sec = _engine.PrerollSeconds;

                    // 试听中：显示「当前 / 总时长」，副行显示文件格式，保持白色（跟录音无关）
                    if (_reader != null)
                    {
                        try
                        {
                            var cur = _reader.CurrentTime;
                            var tot = _reader.TotalTime;
                            TimerText.Text = string.Format("{0:00}:{1:00}:{2:000}/{3:00}:{4:00}:{5:000}",
                                (int)cur.TotalMinutes, cur.Seconds, cur.Milliseconds,
                                (int)tot.TotalMinutes, tot.Seconds, tot.Milliseconds);
                            TimerText.Foreground = WavePalette.IdleText;
                            PrerollText.Text = _previewInfo;
                        }
                        catch { }
                    }
                    else if (sec > 0.05)
                    {
                        // 预录开着：绿色跟着缓存量读秒，满了自然停在设置值上
                        TimerText.Text = FormatMs(TimeSpan.FromSeconds(sec));
                        TimerText.Foreground = WavePalette.PrerollText;
                        PrerollText.Text = "预录中";
                    }
                    else
                    {
                        // 预录关着：白色初始态
                        TimerText.Text = "00:00:000";
                        TimerText.Foreground = WavePalette.IdleText;
                        PrerollText.Text = "";
                    }

                }

                // 试听播完检测：轮询 PlaybackState，而不是靠 PlaybackStopped 事件
                // （内部 seek 的 Stop 也会触发那个事件，异步到达时已经分辨不出来）
                if (_player != null && _reader != null
                    && _player.PlaybackState == PlaybackState.Stopped)
                {
                    Log.Write("[播放] 检测到已停止，收尾");
                    StopPlayback();
                    SetStatus("试听结束");
                }

                // 试听播放头指针（平滑推进，不用会跳变的 _reader.CurrentTime）
                if (_player != null && _reader != null && Waveform.IsStatic)
                {
                    Waveform.SetPlayProgress(AdvancePlayhead());
                }
                else
                {
                    Waveform.ClearPlayProgress();
                }

            }
            catch (Exception ex)
            {
                // 这个函数每 16ms 跑一次，异常全被吃在这里；
                // 不记日志的话，界面卡住、状态错乱时一点线索都没有。
                // 每 5 秒最多记一条，避免异常持续抛出时刷爆日志。
                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                if (nowTicks - _lastUiErrTicks > System.Diagnostics.Stopwatch.Frequency * 5)
                {
                    _lastUiErrTicks = nowTicks;
                    Log.Error("UiTimer_Tick", ex);
                }
            }
        }

        private void SetStatus(string text) => StatusText.Text = text;
    }
}
