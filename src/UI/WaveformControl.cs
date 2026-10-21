using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RecLite.Audio;

namespace RecLite
{
    /// <summary>
    /// 波形控件（位图绘制版）。
    ///
    /// 为什么用 WriteableBitmap：旧版每帧创建 StreamGeometry 矢量图形，
    /// 点数一多就卡。现在直接往像素缓冲里写，一帧只需要一次 WritePixels，
    /// 性能提升一个数量级。
    ///
    /// 波形本体走像素，参考线/刻度文字/播放头仍用矢量叠加（清晰且开销小）。
    ///
    /// 交互：
    ///   滚轮          横轴缩放
    ///   Shift + 滚轮   纵轴缩放
    ///   双击          复位
    /// 削波：逐列判断，只有真正达到满幅的那一列标红。
    /// </summary>
    public class WaveformControl : FrameworkElement
    {
        private const double DefaultPixelsPerPoint = 1.0;
        private const double MinPixelsPerPoint = 0.05;
        private const double MaxPixelsPerPoint = 40.0;

        /// <summary>
        /// 对数刻度的显示下限（dB）。滚轮在 LOG 模式下改这个值，
        /// 就等于「放大某一段 dB」：下限抬高 → 高 dB 段被拉开；下限压低 → 低 dB 段被拉开。
        /// </summary>
        private double _dbFloor = DefaultDbFloor;

        private const double DefaultDbFloor = -60.0;
        private const double MinDbFloor = -120.0;
        private const double MaxDbFloor = -12.0;
        // 削波判定阈值 —— 按当前位深的满幅值算（见 ClipLevel.FullScale）。
        // 默认取 16 bit 满幅；设备打开后由 SetClipThreshold 换成实际位深的值，
        // 这样 16 / 24 / 32 浮点三种格式的红色都准确地落在各自的 0 dBFS 上。
        private double _clipThresh = 32767.0 / 32768.0;
        private double _warnThresh = (32767.0 / 32768.0) * ClipLevel.WarnRatio;

        // ---------- 颜色（Pbgra32 字节序，int 值 = 0xAARRGGBB）----------
        private const int C_Bg = unchecked((int)0xFF0F0F14);
        private const int C_ZeroDb = unchecked((int)0xFF2E2418);
        private const int C_DbGrid = unchecked((int)0xFF1A2222);
        private const int C_Divider = unchecked((int)0xFF3A3A50);
        private const int C_Wave = unchecked((int)0xFF5B8CFF);
        private const int C_StaticWave = unchecked((int)0xFF3DD598);
        private const int C_Clip = unchecked((int)0xFFFF4545);
        private const int C_NearClip = unchecked((int)0xFFFF6B3D);   // 橙红 —— near-clip 预警（−1 dBFS）

        /// <summary>0 dB 参考线（画在波形之上，整片削波时也能看清过载线在哪）</summary>
        private static readonly Pen ZeroDbOverlayPen = Freeze(new Pen(
            new SolidColorBrush(Color.FromArgb(0x66, 0xA8, 0xA8, 0xC8)), 1));

        private double _pixelsPerPoint = DefaultPixelsPerPoint;
        private bool _useLogScale;

        private readonly List<PeakPoint> _points = new List<PeakPoint>();
        private PeakPoint[] _staticPoints;
        private int _channels = 2;
        // 只保留最近 8000 个点（窗口最多显示约 800 点，放大时更少；8000 足够回看）。
        // 之前设 200000 时，每帧 RemoveRange 要把 20 万元素整体前移，会引发明显卡顿。
        // 波形数据点上限。真值定义在 WaveformAnalyzer.MaxPoints（预录上限要靠它反推）：
        // 约 66.67 点/秒，40000 点 ≈ 10 分钟。
        // 拉大几乎不增加每帧开销 —— 渲染是「按像素列遍历，每列只累加该列覆盖的可见点」，
        // 成本只跟可见点数有关，跟库存总数无关；内存每点约 96 字节，40000 点 ≈ 3.8MB。
        private const int MaxPoints = WaveformAnalyzer.MaxPoints;
        private double _playProgress = -1;

        // ---------- 平滑滚动状态（解决「数据一坨一坨到达」导致的跳动）----------
        private double _displayPos;                          // 当前显示到第几个点（浮点，匀速推进）
        private double _targetPos;                           // 实际已有多少点

        /// <summary>录音暂停中：数据显示位置钉死，不再追平滑存货。</summary>
        private bool _paused;
        private double _pointsPerSecond = 46.875;            // 波形点产生速率（sampleRate / 每点采样数）
        // 显示位置落后数据目标的点数 = 留给「匀速推进」的存货。
        // 音频按批到达（实测每 ~11ms 一批，每批还不一定凑满一个波形点），
        // 存货太少（比如 2 点≈30ms）就会频繁「走一步、停下等数据」，看起来就是顿挫。
        // 4 点 ≈ 60ms 是延迟与平顺的折中：足够覆盖两批数据的间隔波动。
        private double _smoothDelayPoints = 4;
        private bool _staticAutoFit = true;                  // 静态波形自动缩放到铺满宽度

        /// <summary>
        /// 静态波形的显示起点（点索引）。实时波形永远是「末尾对齐」，
        /// 静态波形要支持「以鼠标位置为锚点缩放」，所以起点必须可变。
        /// </summary>
        private double _staticStart;
        private double _livePixelsPerPoint = DefaultPixelsPerPoint;   // 实时模式的缩放（试听后要恢复）
        // 实时模式的声道数（试听后要恢复）。SetStatic 会把 _channels 改成文件自己的声道数，
        // 之前退出试听没还原，于是试听一个单声道文件后，实时波形会塌成单声道显示 —— 错。
        private int _liveChannels = 2;

        private WriteableBitmap _bmp;
        private int[] _px = new int[0];
        private int _pw, _ph;

        // ---------- 增量渲染状态（B 方案）----------
        private double _renderedPos = -1;     // 上次绘制时的显示位置（-1 强制全量）
        private double _lastPP = -1;          // 上次横向缩放
        private double _lastVZ = -1;          // 上次纵向缩放
        private bool _lastLog;                // 上次是否对数刻度
        private bool _lastStatic;             // 上次是否静态（试听）模式
        private long _renderCount;            // 累计重绘次数（用于自检统计）

        private static readonly Pen ZeroDbPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x2E, 0x1E)), 1));
        private static readonly Pen DbGridPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x1E, 0x28, 0x28)), 1));
        private static readonly Pen DividerPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x66)), 2));
        private static readonly Pen PlayheadPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x3D)), 2));
        private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xE6)));

        private static readonly Typeface Mono = new Typeface("Consolas");
        private static readonly Typeface Ui = new Typeface("Microsoft YaHei UI");

        private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        public WaveformControl()
        {
            Focusable = true;
            ClipToBounds = true;
        }

        public bool IsStatic => _staticPoints != null;
        public double PixelsPerPoint => _pixelsPerPoint;
        public double VerticalZoom => _curveGamma;
        public bool UseLogScale
        {
            get { return _useLogScale; }
            set { if (_useLogScale != value) { _useLogScale = value; InvalidateVisual(); } }
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            return new PointHitTestResult(this, hitTestParameters.HitPoint);
        }

        public void Configure(int channels)
        {
            _channels = Math.Max(1, channels);
            _points.Clear();
            _staticPoints = null;
            _playProgress = -1;
            _displayPos = 0;
            _targetPos = 0;
            _renderedPos = -1;
            InvalidateVisual();
        }

        public void Append(PeakPoint[] pts)
        {
            if (_staticPoints != null) return;
            if (pts == null || pts.Length == 0) return;
            _points.AddRange(pts);

            // 攒够一批再裁剪，避免每帧都搬运整个列表
            if (_points.Count > MaxPoints + 2000)
                _points.RemoveRange(0, _points.Count - MaxPoints);

            _targetPos = _points.Count;
        }

        /// <summary>设置波形点产生速率（点/秒），用于计算每帧应推进的像素</summary>
        public void SetScrollSpeed(int sampleRate, int samplesPerPoint)
        {
            if (sampleRate > 0 && samplesPerPoint > 0)
                _pointsPerSecond = sampleRate / (double)samplesPerPoint;
        }

        /// <summary>
        /// 每帧调用：让显示位置匀速追赶数据位置。
        /// 这是「数据一坨一坨到达」时保持滚动顺滑的关键。
        /// </summary>
        /// <param name="dtSeconds">距上一帧的秒数</param>
        /// <summary>
        /// 录音暂停 / 恢复。
        ///
        /// 暂停时**只是不再推进显示位置**，绝不把 _displayPos 跳到 _targetPos ——
        /// 跳过去等于把「平滑存货」那一段一次性画出来，视觉上就是波形突然伸出一截。
        /// 停在这里之后，剩下的存货等恢复录音后再平滑追上去。
        /// </summary>
        public void SetPaused(bool paused)
        {
            if (_paused == paused) return;
            _paused = paused;
        }

        /// <summary>
        /// 每帧调用：让显示位置匀速追赶数据位置（平滑滚动）。
        /// </summary>
        public void TickAnimation(double dtSeconds)
        {
            if (_staticPoints != null) { _displayPos = _staticPoints.Length; _targetPos = _displayPos; return; }

            // 录音暂停：原地冻结，一帧都不再推进。
            // 这样波形和时间轴是同一瞬间停住的，画面也不会因为跳位而多出一截。
            if (_paused) return;

            if (dtSeconds <= 0) return;

            double step = _pointsPerSecond * dtSeconds;
            double safePos = _targetPos - _smoothDelayPoints;   // 前面要留够「存货」才能匀速推进
            if (safePos < 0) safePos = 0;

            // 严重落后（切设备/清空/刚结束试听）：直接跳到缓冲位
            if (_displayPos < safePos - 40 || _displayPos > _targetPos)
            {
                _displayPos = safePos;
                InvalidateVisual();
                return;
            }

            if (_displayPos >= safePos)
            {
                // 存货不够，画面只能停在这里 —— 如果这次停很久，就是用户看到的「波形冻住」。
                return;
            }

            // 纯时间推进 —— 这是消除顿挫的关键。
            // 旧写法是「追到安全位为止」(move = min(safePos - display, step))，
            // 而音频是按批到达的（实测每 11ms 一批，每批还不一定满一个点），
            // 于是 display 经常「走一步就撞到安全位、停下等下一批」→ 画面顿挫。
            // 改成只按经过时间推进：速度恒定，存货够就一直滑。
            _displayPos += step;
            if (_displayPos > safePos) _displayPos = safePos;

            // 每帧都请求重绘，但「要不要重算像素」交给 NeedRepaint 按整数点判断。
            //
            // 为什么不能只在跨点时请求：实测显示每秒推进 66.7 点，但重绘只有 59 次 ——
            // WPF 的帧间隔本身在抖（133±5Hz），跨点请求会被合并掉一部分，
            // 于是每次重绘平均前进 1.13 像素（有时 1、有时 2），看着就是顿挫。
            // 现在把「提交绘制」和「重算像素」解耦：绘制每帧都提交，
            // 像素只在整数点变化时重算，画面位移永远是整整 1 像素。
            InvalidateVisual();
        }

        /// <summary>立刻跳到最新位置（切换设备/清空后使用）</summary>
        public void SnapToLatest()
        {
            _displayPos = _targetPos;
            _renderedPos = -1;
            InvalidateVisual();
        }

        public void SetStatic(PeakPoint[] points, int channels)
        {
            // 只在「从实时切进静态」这一次记录，重复 SetStatic（自检里会连调两次）不能覆盖，
            // 否则第二次记下的就是静态模式自己的缩放/声道数，退出试听还原成错的。
            if (_staticPoints == null)
            {
                _livePixelsPerPoint = _pixelsPerPoint;
                _liveChannels = _channels;
            }
            _channels = Math.Max(1, channels);
            _staticPoints = points ?? new PeakPoint[0];
            _displayPos = _staticPoints.Length;   // 静态波形一次显示全部，不滚动
            _targetPos = _displayPos;
            _staticStart = 0;
            _staticAutoFit = true;                // 下次绘制时自动缩放到铺满宽度
            _renderedPos = -1;
            InvalidateVisual();
        }

        public void ClearStatic()
        {
            if (_staticPoints == null) return;
            _staticPoints = null;
            _pixelsPerPoint = _livePixelsPerPoint;   // 恢复到实时模式的缩放，避免"试听放大后回不去"
            _channels = _liveChannels;               // 同样恢复声道数，否则试听单声道文件后实时波形塌成单声道
            _staticAutoFit = false;
            _staticStart = 0;
            _displayPos = _targetPos;
            _renderedPos = -1;
            InvalidateVisual();
        }

        public void Clear()
        {
            _points.Clear();
            _displayPos = 0;
            _targetPos = 0;
            _renderedPos = -1;
            InvalidateVisual();
        }

        /// <summary>
        /// 计算当前该画哪一段。实时和静态/黄条/点击定位全部走这一个函数，
        /// 保证坐标永远一致（原来四处各写一份，改一处漏一处就会错位）。
        ///
        /// 实时：末尾对齐（最新数据在右边）。
        /// 静态：靠左对齐，起点由 _staticStart 控制（支持锚点缩放）。
        /// </summary>
        private void GetRange(double w, int total, out int start, out int count, out double firstCol)
        {
            double ppp = _pixelsPerPoint;
            if (ppp < 1e-6) ppp = 1e-6;
            int maxVisible = Math.Max(1, (int)(w / ppp));

            if (_staticPoints != null && total > 0)
            {
                int s = (int)Math.Round(_staticStart);
                int maxStart = Math.Max(0, total - maxVisible);
                if (s < 0) s = 0;
                if (s > maxStart) s = maxStart;
                start = s;
                count = Math.Min(total - s, maxVisible);
                firstCol = 0;
            }
            else
            {
                int displayCount = Math.Min(total, (int)Math.Floor(_displayPos));
                int c = Math.Min(displayCount, maxVisible);
                start = displayCount - c;
                count = c;
                firstCol = w - c * ppp;
            }
        }

        public void ZoomBy(double factor)
        {
            ZoomBy(factor, -1);
        }

        /// <summary>
        /// 横向缩放。anchorX ≥ 0 时以该屏幕位置为锚点（鼠标所在处的内容保持不动），
        /// 否则以控件中心为锚点。静态波形还限制「最小 = 整首铺满」，避免右侧出现空白。
        /// </summary>
        public void ZoomBy(double factor, double anchorX)
        {
            double w = ActualWidth;
            int total = _staticPoints != null ? _staticPoints.Length : 0;

            // 当前有效缩放（auto-fit 时像素/点是按铺满算的）
            double curPpp = _pixelsPerPoint;
            bool isStatic = _staticPoints != null && total > 0;
            if (isStatic && w > 0 && _staticAutoFit)
                curPpp = w / total;
            if (curPpp < 1e-6) curPpp = 1e-6;

            // 静态波形的最低下限 = 铺满整宽，再小就会在右边留出空白
            double minPpp = MinPixelsPerPoint;
            if (isStatic && w > 0)
                minPpp = Math.Max(minPpp, w / total);

            double newPpp = curPpp * factor;
            if (newPpp < minPpp) newPpp = minPpp;
            if (newPpp > MaxPixelsPerPoint) newPpp = MaxPixelsPerPoint;

            if (Math.Abs(newPpp - curPpp) < 1e-9)
            {
                _pixelsPerPoint = newPpp;
                _staticAutoFit = false;
                return;
            }

            if (isStatic && w > 0)
            {
                // 锚点：鼠标处的内容在缩放前后保持同一屏幕位置
                double ax = (anchorX >= 0 && anchorX <= w) ? anchorX : w / 2;
                double anchorPoint = _staticStart + ax / curPpp;
                _staticStart = anchorPoint - ax / newPpp;

                int maxVisible = Math.Max(1, (int)(w / newPpp));
                double maxStart = Math.Max(0, total - maxVisible);
                if (_staticStart < 0) _staticStart = 0;
                if (_staticStart > maxStart) _staticStart = maxStart;
            }

            _pixelsPerPoint = newPpp;
            _staticAutoFit = false;
            _renderedPos = _displayPos - 1000;   // 缩放变了，整屏重算
            InvalidateVisual();
        }

        /// <summary>
        /// 纵轴曲线强度 γ。
        /// γ = 1 是线性；γ > 1 把低电平压扁、把 0 dB 附近舒展（放大高 dB 段）；
        /// γ &lt; 1 反过来，放大低 dB 段。0 dB 线和 +6 dB 顶沿始终不动。
        /// </summary>
        private double _curveGamma = 1.0;

        private const double MinCurveGamma = 0.4;
        private const double MaxCurveGamma = 2.6;

        public double CurveGamma { get { return _curveGamma; } }

        /// <summary>调整纵轴曲线：factor &gt; 1 放大高 dB 段，factor &lt; 1 放大低 dB 段。</summary>
        public void ZoomVerticalBy(double factor)
        {
            if (_useLogScale)
            {
                // LOG 模式：改「显示下限」，等于把某一段 dB 拉开。
                // 上滚 → 下限抬高 → 0 dB 附近被展开（放大高 dB 段）；
                // 下滚 → 下限压低 → 能看到更安静的部分。
                double span = (0.0 - _dbFloor) / factor;
                double floor = 0.0 - span;
                if (floor < MinDbFloor) floor = MinDbFloor;
                if (floor > MaxDbFloor) floor = MaxDbFloor;
                if (Math.Abs(floor - _dbFloor) < 1e-9) return;
                _dbFloor = floor;
            }
            else
            {
                var v = _curveGamma * factor;
                if (v < MinCurveGamma) v = MinCurveGamma;
                if (v > MaxCurveGamma) v = MaxCurveGamma;
                if (Math.Abs(v - _curveGamma) < 1e-9) return;
                _curveGamma = v;
            }

            _renderedPos = _displayPos - 1000;   // 曲线/范围变了，整屏重算一次
            InvalidateVisual();
        }

        /// <summary>只复位横向缩放（时间轴用它）。</summary>
        public void ResetZoomX()
        {
            _pixelsPerPoint = DefaultPixelsPerPoint;
            _staticStart = 0;
            if (_staticPoints != null) _staticAutoFit = true;
            _renderedPos = _displayPos - 1000;
            InvalidateVisual();
        }

        /// <summary>只复位纵向缩放（dB 刻度条用它）。</summary>
        public void ResetZoomY()
        {
            _curveGamma = 1.0;
            _dbFloor = DefaultDbFloor;
            _renderedPos = _displayPos - 1000;
            InvalidateVisual();
        }

        /// <summary>两个方向一起复位。</summary>
        public void ResetZoom()
        {
            _pixelsPerPoint = DefaultPixelsPerPoint;
            _curveGamma = 1.0;
            _dbFloor = DefaultDbFloor;
            _staticStart = 0;
            if (_staticPoints != null) _staticAutoFit = true;
            _renderedPos = _displayPos - 1000;
            InvalidateVisual();
        }

        public void SetPlayProgress(double p)
        {
            // 拖动预览期间由鼠标全权控制黄条，播放进度不许插进来
            if (_seekPreview) return;
            if (p < 0) p = 0; if (p > 1) p = 1;
            // 阈值放小：60fps 下每帧进度变化约 0.0002~0.002，阈值太大会被跳过导致指针一跳一跳
            if (Math.Abs(p - _playProgress) < 0.00002) return;
            _playProgress = p;
            InvalidateVisual();
        }

        /// <summary>用户在静态波形上点击 / 拖动定位时触发，参数是 (0~1 相对时间, 是否松手提交)。</summary>
        public event Action<double, bool> SeekRequested;

        private bool _seeking;
        private bool _dragStarted;      // 移动是否已超过 4px（之后才跟随鼠标）
        private double _downX;

        /// <summary>
        /// 正在拖动预览：这段时间里黄条位置完全由鼠标决定，
        /// 播放进度（SetPlayProgress）不许覆盖它 —— 否则按着不动时黄条会自己往后爬。
        /// </summary>
        private bool _seekPreview;

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            // 双击在波形区不做任何事（复位缩放由刻度条 / 时间轴负责）
            if (e.ClickCount == 2) return;

            if (_staticPoints != null && SeekRequested != null)
            {
                _seeking = true;
                _dragStarted = false;
                _downX = e.GetPosition(this).X;
                CaptureMouse();
                RaiseSeek(_downX, false);   // 黄条立即跳到按下的位置（只预览，不 seek）
                e.Handled = true;
                return;
            }

            Focus();
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_seeking) { base.OnMouseMove(e); return; }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _seeking = false;
                _dragStarted = false;
                _seekPreview = false;
                if (IsMouseCaptured) ReleaseMouseCapture();
                return;
            }

            double x = e.GetPosition(this).X;

            // 按着不动（抖动 < 4px）时黄条停在按下的位置；
            // 超过 4px 才开始跟随鼠标，之后一直跟着，直到松手。
            if (!_dragStarted)
            {
                if (Math.Abs(x - _downX) < 4) return;
                _dragStarted = true;
            }

            RaiseSeek(x, false);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!_seeking) { base.OnMouseLeftButtonUp(e); return; }

            // 先解除冻结，再提交跳转（全程只在这里 seek 一次）
            _seekPreview = false;
            _seeking = false;
            _dragStarted = false;

            RaiseSeek(e.GetPosition(this).X, true);
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
        }

        /// <summary>
        /// 控件内的 x 坐标 → 0~1 的相对时间。
        /// 静态波形缩放过之后不再铺满（只显示末尾一段），
        /// 所以必须按「当前可见起点 + 可见点数」换算，否则点哪儿都会偏。
        /// </summary>
        private double XToRatio(double x)
        {
            if (_staticPoints == null || _staticPoints.Length <= 0) return 0;

            double w = ActualWidth;
            if (w <= 0) return 0;

            double total = _staticPoints.Length;

            // auto-fit 时把缩放同步成铺满值，GetRange 才能算对
            if (_staticAutoFit) _pixelsPerPoint = w / total;
            if (_pixelsPerPoint <= 0) _pixelsPerPoint = 1;

            int start, count;
            double firstCol;
            GetRange(w, (int)total, out start, out count, out firstCol);

            double idx = start + (x - firstCol) / _pixelsPerPoint;
            double ratio = idx / total;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            return ratio;
        }

        private void RaiseSeek(double x, bool commit)
        {
            double ratio = XToRatio(x);

            // commit=false 是拖动预览：冻结黄条的自动推进，并由这里直接写黄条位置。
            // 不能只靠 MainWindow 回调去设 —— 那个入口会被 _seekPreview 守卫挡掉，
            // 结果就是「按下后黄条锁在原地、拖动也不跟」。
            _seekPreview = !commit;
            if (!commit)
            {
                _playProgress = ratio;
                InvalidateVisual();
            }

            var h = SeekRequested;
            if (h != null) h(ratio, commit);
        }

        public void ClearPlayProgress()
        {
            if (_playProgress < 0) return;
            _playProgress = -1;
            InvalidateVisual();
        }

        // ---------- 交互 ----------

        /// <summary>
        /// 控件尺寸变化时强制整屏重画。
        /// 静态波形的 _displayPos 是固定的，永远不会触发「位置变了才重画」的判据，
        /// 于是位图按新宽度重建了、内容却还是旧的 → 表现为波形缺失、裁切或闪烁。
        /// </summary>
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            _renderedPos = -1;
            InvalidateVisual();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            double f = e.Delta > 0 ? 1.25 : 1 / 1.25;
            if (shift) ZoomVerticalBy(f);
            else ZoomBy(f, e.GetPosition(this).X);   // 以鼠标所在位置为锚点
            e.Handled = true;
        }

        // ---------- 映射 ----------

        // ---------- 纵向显示范围 ----------
        //
        // 整个声道带就是显示范围：0 dBFS 钉在带顶，底部是 −∞（LIN）或 _dbFloor（LOG）。
        // 不再保留 +6 dB 区 —— 数字源（ADC / ADAT）物理上不可能超过满幅，
        // 超过 1.0 只可能来自软件增益，那部分直接贴顶、靠红色表达即可。
        private const double BandEdgeMargin = 0.94;     // 上下各留一点边距，避免贴住描边

        /// <summary>声道带的显示半径（中心线到带顶的可用像素）</summary>
        private static double BandRadius(double bandH)
        {
            return bandH / 2 * BandEdgeMargin;
        }

        /// <summary>
        /// 振幅 → 带内归一化位置（0 = 中心线，1 = 带顶 = 0 dBFS）。
        /// LIN 走 γ 曲线（γ=1 线性，γ 大压高 dB 段），LOG 走 dB 等距。
        /// </summary>
        private double MapAmpNorm(double v)
        {
            double lin = Math.Abs(v) / _clipThresh;
            if (!(lin > 0)) return 0;
            if (lin > 1.0) lin = 1.0;           // 超过满幅只贴顶，不额外占空间

            if (!_useLogScale)
                return (_curveGamma == 1.0) ? lin : Math.Pow(lin, _curveGamma);

            double db = 20.0 * Math.Log10(lin);
            if (db < _dbFloor) return 0;
            return (db - _dbFloor) / (0.0 - _dbFloor);
        }

        private double MapAmp(double v)
        {
            if (v < 0) v = -v;
            if (v > 1) v = 1;
            if (!_useLogScale) return v;
            if (v < 1e-6) return 0;
            double db = 20.0 * Math.Log10(v);
            if (db < _dbFloor) return 0;
            return (db - _dbFloor) / (0.0 - _dbFloor);
        }

        private double DbToRatio(double db)
        {
            if (!_useLogScale)
            {
                double lin = Math.Pow(10, db / 20.0);
                return lin < 0 ? 0 : (lin > 1 ? 1 : lin);
            }
            if (db < _dbFloor) return 0;
            return (db - _dbFloor) / (0.0 - _dbFloor);
        }

        /// <summary>
        /// 设置削波判定阈值（由当前输出位深的满幅值决定）。
        /// 换阈值会强制整屏重上一遍色，因为缓冲里已有的像素是按旧阈值染的。
        /// </summary>
        public void SetClipThreshold(double fullScale)
        {
            if (!(fullScale > 0)) return;
            if (Math.Abs(fullScale - _clipThresh) < 1e-12) return;

            _clipThresh = fullScale;
            _warnThresh = fullScale * ClipLevel.WarnRatio;

            // 把「已渲染位置」往回挪一大截，NeedRepaint 的整数点比较就会立刻返回 true，
            // 下一帧整屏重算颜色；之后照常按点增量重绘，不会每帧全刷。
            _renderedPos = _displayPos - 1000;
            InvalidateVisual();
        }

        public double MapAmpForTest(double v) { return MapAmp(v); }
        public int PointCountForTest { get { return _staticPoints != null ? _staticPoints.Length : _points.Count; } }
        public bool StaticForTest { get { return _staticPoints != null; } }

        // ---------- 绘制 ----------

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 1 || h < 1) return;

            int iw = (int)w, ih = (int)h;
            try
            {
                EnsureBitmap(iw, ih);

                if (NeedRepaint(iw, ih))
                {
                    RenderPixels(iw, ih);
                    _renderCount++;
                }

                dc.DrawImage(_bmp, new Rect(0, 0, w, h));
                DrawOverlay(dc, w, h);
            }
            catch (Exception ex)
            {
                try { Core.Log.Write("[WaveformControl.OnRender] " + ex.GetType().Name + ": " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// B 方案核心：判断这一帧是否真的需要重画像素。
        /// 无新数据、且缩放/模式/尺寸都没变 → 直接复用上一帧的位图，零开销。
        /// </summary>
        private bool NeedRepaint(int w, int h)
        {
            if (_renderedPos < 0) return true;
            // 只在「整数点」真正变化时才重算像素 —— 一个点正好 1 像素，
            // 这样每次重算的画面位移永远是整整 1 像素，不会出现 1.13 像素这种小数步进。
            if ((int)Math.Floor(_displayPos) != (int)Math.Floor(_renderedPos)) return true;
            if (_lastPP != _pixelsPerPoint) return true;                    // 横向缩放变了
            if (_lastVZ != _curveGamma) return true;                        // 纵轴曲线变了
            if (_lastLog != _useLogScale) return true;                      // 刻度模式变了
            if (_lastStatic != IsStatic) return true;                       // 实时/静态切换
            return false;
        }

        /// <summary>累计像素重绘次数（自检统计用）</summary>
        public long RenderCountForTest => _renderCount;


        // ---- 时间轴需要的数据 ----
        public bool IsStaticMode { get { return _staticPoints != null; } }
        public int StaticTotalPoints { get { return _staticPoints != null ? _staticPoints.Length : 0; } }
        public double StaticStart { get { return _staticStart; } }
        public double PixelsPerPointNow { get { return _pixelsPerPoint; } }
        public double PointsPerSecondNow { get { return _pointsPerSecond; } }

        /// <summary>静态波形对应的文件总时长（秒），由 MainWindow 在加载文件时设置。</summary>
        public double StaticDurationSeconds { get; set; }

        /// <summary>当前声道数（刻度条要按声道分组标注）。</summary>
        public int ChannelCountNow { get { return Math.Max(1, _channels); } }

        /// <summary>
        /// 某个 dB 值在指定声道里的 y 坐标。供波形外的独立刻度条使用，
        /// 和波形绘制共用 MapAmpNorm，所以标注和实际波形永远对齐。
        /// </summary>
        public double DbToY(int channel, double db)
        {
            double h = ActualHeight;
            int ch = ChannelCountNow;
            double bandH = h / ch;
            double radius = BandRadius(bandH);
            double cy = bandH * channel + bandH / 2;
            double t = MapAmpNorm(Math.Pow(10.0, db / 20.0) * _clipThresh);
            return cy - t * radius;
        }

        private void EnsureBitmap(int w, int h)
        {
            if (_bmp != null && _pw == w && _ph == h) return;
            _pw = w; _ph = h;
            _bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
            _px = new int[w * h];
        }

        private void RenderPixels(int w, int h)
        {
            var px = _px;
            int n = w * h;

            // 1. 背景
            for (int i = 0; i < n; i++) px[i] = C_Bg;

            int ch = _channels;
            double bandH = (double)h / ch;

            var source = (IReadOnlyList<PeakPoint>)_staticPoints ?? _points;

            // 每个声道的参考线（只画横向线条到像素缓冲，文字在 overlay）
            // +6 dB 在带顶，往下依次是 0 / −6 / …，位置全部走同一条 MapAmpNorm
            double[] dbLines = { 0, -6, -12, -18, -24 };
            double radius = BandRadius(bandH);
            for (int c = 0; c < ch; c++)
            {
                double cy = bandH * c + bandH / 2;

                foreach (var db in dbLines)
                {
                    double t = MapAmpNorm(Math.Pow(10.0, db / 20.0) * _clipThresh);
                    int yt = (int)Math.Round(cy - t * radius);
                    int yb = (int)Math.Round(cy + t * radius);
                    int col = db == 0 ? C_ZeroDb : C_DbGrid;
                    DrawHLine(px, w, h, yt, col);
                    DrawHLine(px, w, h, yb, col);
                }
            }

            // 3. 声道分割线
            for (int c = 1; c < ch; c++)
            {
                int y = (int)Math.Round(bandH * c);
                DrawHLine(px, w, h, y, C_Divider);
                DrawHLine(px, w, h, y + 1, C_Divider);
            }

            // 4. 波形：逐「像素列」绘制（放大时同一个点会铺满多列）
            if (source != null && source.Count > 0)
            {
                // 静态波形（试听）：自动缩放到正好铺满整个宽度，避免短文件左边留大片空白
                if (_staticPoints != null && _staticAutoFit)
                {
                    double fit = (double)w / source.Count;
                    if (fit < MinPixelsPerPoint) fit = MinPixelsPerPoint;
                    if (fit > MaxPixelsPerPoint) fit = MaxPixelsPerPoint;
                    _pixelsPerPoint = fit;
                }

                int start, count;
                double firstCol;
                GetRange(w, source.Count, out start, out count, out firstCol);

                for (int col = 0; col < w; col++)
                {
                    // 该像素列覆盖哪些波形点（放大时 1 列=1 点；缩小时 1 列可能覆盖多个点）
                    int idx = (int)Math.Floor((col - firstCol) / _pixelsPerPoint);
                    if (idx < 0 || idx >= count) continue;

                    int idxEnd = idx + 1;
                    if (_pixelsPerPoint < 1.0)
                    {
                        idxEnd = (int)Math.Floor((col + 1 - firstCol) / _pixelsPerPoint);
                        if (idxEnd <= idx) idxEnd = idx + 1;
                        if (idxEnd > count) idxEnd = count;
                    }

                    for (int c = 0; c < ch; c++)
                    {
                        double cy = bandH * c + bandH / 2;

                        // 合并本列覆盖的所有点，保留峰值（缩小时不丢波峰）
                        double mx = 0, mn = 0;
                        bool clipped = false;
                        int consecClip = 0;      // 连续满幅计数
                        for (int k = idx; k < idxEnd; k++)
                        {
                            var pp = source[start + k];
                            if (pp == null) continue;
                            double a = (pp.Max != null && c < pp.Max.Length) ? pp.Max[c] : 0;
                            double b = (pp.Min != null && c < pp.Min.Length) ? pp.Min[c] : 0;
                            if (a > mx) mx = a;
                            if (b < mn) mn = b;
                            // 削波判定：达到满幅即算（统一一份逻辑）。
                            // 试听时由调用方把阈值抬高一点点（见 MainWindow.PlayFile 里的 ×(1+1e-6)），
                            // 这样商业母带里「正好压在 0 dBFS」的峰值不会被误判，而录音侧照旧抓住真削波。
                            if (a >= _clipThresh || b <= -_clipThresh)
                            {
                                if (++consecClip >= 3) clipped = true;
                            }
                            else consecClip = 0;
                        }

                        // 按这一列的峰值上色，分三档：
                        //  ≥ 满幅  → 纯红：真正的削波，红色只在这一档出现
                        //  ≥ −1 dB → 橙红：near-clip 预警，该收手了但还没爆
                        //  更低    → 256 级渐变表，按预警线归一化后铺满整条动态范围
                        // 试听（静态）波形保持单色绿，方便一眼区分「这是文件」不是「实时」；
                        // 但削波红排在它前面 —— 文件本身爆了也应该看得见
                        int color;
                        if (clipped) color = C_Clip;
                        else if (_staticPoints != null) color = C_StaticWave;
                        else
                        {
                            // 归一化到 near-clip 线：不同位深下渐变都铺满整个可用区间，
                            // 红色位置则严格对应各自的 0 dBFS，不会被提前占掉。
                            double peakAmp = Math.Max(mx, -mn);
                            if (peakAmp >= _warnThresh) color = C_NearClip;
                            else color = AmpLut[AmpToLutIndex(peakAmp / _warnThresh)];
                        }

                        // 值按输入画、0 dB 线按更严的那条满幅线标位置：
                        // 超过 1.0 倍满幅的峰顶会进入顶部 +6 dB 余量区，冲出去多少就标多少红
                        int yTop = (int)Math.Round(cy - MapAmpNorm(mx) * radius);
                        int yBot = (int)Math.Round(cy + MapAmpNorm(mn) * radius);
                        if (yTop < 0) yTop = 0;
                        if (yBot >= h) yBot = h - 1;
                        if (yTop > yBot) { int sw = yTop; yTop = yBot; yBot = sw; }
                        if (yBot == yTop) yBot = Math.Min(h - 1, yTop + 1);

                        for (int y = yTop; y <= yBot; y++)
                            px[y * w + col] = color;
                    }
                }
            }

            _bmp.WritePixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);

            // 记录本帧状态，供 NeedRepaint 比对
            _renderedPos = _displayPos;
            _lastPP = _pixelsPerPoint;
            _lastVZ = _curveGamma;
            _lastLog = _useLogScale;
            _lastStatic = IsStatic;
        }

        // ---------- 振幅染色：按音量上色，像电平表那样 ----------
        //
        // 做法：启动时把 9 个色标插值成 256 级查找表，渲染时每列只做一次开方 + 查表。
        // 开销极低（每列约几纳秒），比每列现场算颜色便宜得多。
        //
        // 色标刻意「封顶在橙色」—— 纯红只留给削波（C_Clip），
        // 这样正常的大音量不会被误看成爆音，红色才有警示意义。
        private static readonly int[] AmpLut = BuildAmpLut();

        private static int[] BuildAmpLut()
        {
            var stops = new[]
            {
                new { P = 0.00, C = Color.FromRgb(0x2B, 0x5C, 0xC4) },   // 深蓝 —— 很轻
                new { P = 0.18, C = Color.FromRgb(0x2A, 0x86, 0xD8) },   // 蓝
                new { P = 0.34, C = Color.FromRgb(0x2A, 0xB6, 0xC8) },   // 青
                new { P = 0.50, C = Color.FromRgb(0x35, 0xC8, 0x9A) },   // 青绿
                new { P = 0.64, C = Color.FromRgb(0x3D, 0xD5, 0x98) },   // 绿 —— 中段
                new { P = 0.76, C = Color.FromRgb(0x8C, 0xD5, 0x3D) },   // 黄绿
                new { P = 0.87, C = Color.FromRgb(0xD8, 0xD0, 0x35) },   // 黄
                new { P = 0.95, C = Color.FromRgb(0xFF, 0xC1, 0x3D) },   // 琥珀
                new { P = 1.00, C = Color.FromRgb(0xFF, 0x8A, 0x2B) },   // 橙 —— 封顶，不碰红
            };

            var lut = new int[256];
            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0;
                int k = 0;
                while (k < stops.Length - 2 && t > stops[k + 1].P) k++;
                double p0 = stops[k].P, p1 = stops[k + 1].P;
                double f = (p1 - p0) > 1e-9 ? (t - p0) / (p1 - p0) : 0.0;
                if (f < 0) f = 0; if (f > 1) f = 1;
                byte r = (byte)Math.Round(stops[k].C.R + (stops[k + 1].C.R - stops[k].C.R) * f);
                byte g = (byte)Math.Round(stops[k].C.G + (stops[k + 1].C.G - stops[k].C.G) * f);
                byte b = (byte)Math.Round(stops[k].C.B + (stops[k + 1].C.B - stops[k].C.B) * f);
                lut[i] = unchecked((int)(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b));
            }
            return lut;
        }

        /// <summary>
        /// 振幅 → 查找表索引。
        /// 用开方而不是直接线性：线性映射下 -40dB 的声音全挤在 0 附近，颜色几乎没层次；
        /// 开方能拉开小音量的区分度，而它比 log10 快 4~8 倍（每帧要算 2000 次）。
        /// </summary>
        private static int AmpToLutIndex(double amp)
        {
            if (amp <= 0) return 0;
            if (amp > 1) amp = 1;
            int idx = (int)Math.Round(Math.Sqrt(amp) * 255.0);
            if (idx < 0) return 0;
            if (idx > 255) return 255;
            return idx;
        }

        private static void DrawHLine(int[] px, int w, int h, int y, int color)
        {
            if (y < 0 || y >= h) return;
            int off = y * w;
            for (int x = 0; x < w; x++) px[off + x] = color;
        }

        private void DrawOverlay(DrawingContext dc, double w, double h)
        {
            int ch = _channels;
            double bandH = h / ch;
            double dpi = 1.0;
            try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
            if (dpi <= 0) dpi = 1.0;

            // 只画 0 dB 那条线（它在削波时是过载参照，必须看得见）。
            // 其余 dB 标签已经搬到波形左侧的独立刻度条上，这里不再画文字。
            double radius = BandRadius(bandH);
            for (int c = 0; c < ch; c++)
            {
                double cy = bandH * c + bandH / 2;
                double y0 = cy - MapAmpNorm(_clipThresh) * radius;
                dc.DrawLine(ZeroDbOverlayPen, new Point(0, y0), new Point(w, y0));
            }

            // 播放头
            if (_playProgress >= 0)
            {
                // 必须和波形绘制用同一套坐标：
                // 静态波形缩放后只显示末尾一段（start = 总点数 − 可见点数），
                // 直接按 _playProgress * w 算会让黄条和波形对不上。
                double x;
                if (_staticPoints != null && _staticPoints.Length > 0)
                {
                    // 和波形绘制共用 GetRange，保证黄条严丝合缝贴在波形上
                    if (_staticAutoFit) _pixelsPerPoint = w / _staticPoints.Length;
                    int st, cnt;
                    double fc;
                    GetRange(w, _staticPoints.Length, out st, out cnt, out fc);
                    double targetPoint = _playProgress * _staticPoints.Length;
                    x = fc + (targetPoint - st) * _pixelsPerPoint;
                }
                else
                {
                    x = _playProgress * w;
                }

                if (x >= 0 && x <= w)
                    dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, h));
            }

            // 缩放信息放「右上角」。
            // 原来在左上角，切到 LOG(dB) 刻度后左侧的 dB 标签会往上挤，把这一行压住。
            // LIN 模式显示 γ（纵轴曲线：1.00 线性，>1 放大高 dB 段，<1 放大低 dB 段）；
            // LOG 模式显示当前显示下限。
            string info = _useLogScale
                ? string.Format("{0:0.00}x  {1:0} dB  LOG", _pixelsPerPoint, _dbFloor)
                : string.Format("{0:0.00}x  γ{1:0.00}  LIN", _pixelsPerPoint, _curveGamma);
            var ft = GetCachedText(info, 11, Mono, LabelBrush, dpi);
            DrawOutlinedText(dc, ft, new Point(Math.Max(8, w - ft.Width - 8), 5), LabelBrush);
        }

        /// <summary>文字绘制。刻度标签移到波形外的独立刻度条上，这里只保留波形内的注明。</summary>
        private void DrawOutlinedText(DrawingContext dc, FormattedText ft, Point origin, Brush fill)
        {
            dc.DrawText(ft, origin);
        }

        // ---------- 文字缓存（避免每帧重建 FormattedText，这是常见的隐藏性能坑）----------

        private readonly Dictionary<string, FormattedText> _textCache = new Dictionary<string, FormattedText>();
        private double _cachedDpi = -1;

        private FormattedText GetCachedText(string text, double size, Typeface face, Brush brush, double dpi)
        {
            if (Math.Abs(_cachedDpi - dpi) > 0.001)
            {
                _textCache.Clear();
                _cachedDpi = dpi;
            }

            string key = size.ToString("0.0") + "|" + text;
            FormattedText ft;
            if (!_textCache.TryGetValue(key, out ft))
            {
                ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    face, size, brush, dpi);
                if (_textCache.Count > 64) _textCache.Clear();
                _textCache[key] = ft;
            }
            return ft;
        }
    }
}
