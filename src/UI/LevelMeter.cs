using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RecLite
{
    /// <summary>
    /// 电平表（横向 / 纵向，单声道或双声道）。
    ///
    /// 纵向双声道时，条内部裂成左右两条（L / R），共用一套左侧刻度。
    /// 顶部 18% 是 +6 dB 过载区，0 dB 刻度就落在余量区下边界 → 与波形参考线位置一致。
    /// 刻度位置和填充用同一套「从底部往上推」的映射，所以标出来的就是真位置。
    /// </summary>
    public class LevelMeter : FrameworkElement
    {
        private const int MaxChannels = 2;

        /// <summary>峰值保持时长（秒）：到点之前指示器钉在峰值上不动</summary>
        private const double PeakHoldSeconds = 2.0;

        /// <summary>峰值保持结束后的下落速度（每秒落掉多少个满量程）</summary>
        private const double PeakFallPerSecond = 0.3;

        /// <summary>电平柱回落的时间常数（秒）。越大掉得越慢。</summary>
        private const double ReleaseTau = 0.13;

        private const double DefaultDbFloor = -60.0;   // 主流 DAW（PT/Logic/Cubase/Audition）的默认下限
        private const double MinDbFloor = -72.0;       // 最深到 −72（Reaper / Pro Tools HD 的做法）
        private const double MaxDbFloor = -12.0;

        /// <summary>顶部 +6 dB 余量占整条高度的比例（与波形保持一致）</summary>
        private const double HeadroomFraction = 0.10;
        private const double HeadroomMinPx = 10;
        private const double HeadroomMaxFraction = 0.45;

        /// <summary>纵向模式下给刻度文字预留的宽度</summary>
        private const double ScaleWidth = 30;

        /// <summary>双声道时两条之间的间隔</summary>
        private const double ChannelGap = 2;

        // 电平与峰值全程保持 float，既不降到 int16、也不提前 clamp 到 1.0：
        // 32 bit 浮点素材本来就允许超过满幅，先截断就丢掉了「超出了多少」。
        private readonly float[] _level = new float[MaxChannels];
        private readonly float[] _peak = new float[MaxChannels];
        private readonly long[] _peakHoldUntil = new long[MaxChannels];

        private long _lastTick;
        private double _fullScale = 32767.0 / 32768.0;

        /// <summary>刻度显示下限（dB）。滚轮可调，范围越窄 0 dB 附近被拉得越开。</summary>
        private double _dbFloor = DefaultDbFloor;

        /// <summary>
        /// 振幅归零/最低绘制阈值：比显示下限再低 6 dB。
        /// 原来是写死的 0.003（−50.5 dB），而刻度能到 −60，于是柱子还没落到底就整根消失。
        /// 现在跟着 _dbFloor 走，柱子就能一路平滑落到底部。只在滚轮改范围时重算，绘制路径上零开销。
        /// </summary>
        private float _zeroFloor = (float)Math.Pow(10.0, (DefaultDbFloor - 6.0) / 20.0);
        private double _lastDpi = 1.0;
        private int _channelCount = 1;

        /// <summary>纵向排列（竖条）</summary>
        public bool IsVertical { get; set; }

        /// <summary>在条的左侧画 dB 刻度（两条表并排时只给左边那条开）</summary>
        public bool ShowScale { get; set; }

        /// <summary>显示几个声道（1 或 2）；双声道时条内裂成 L / R 两条</summary>
        public int ChannelCount
        {
            get { return _channelCount; }
            set
            {
                int v = value < 1 ? 1 : (value > MaxChannels ? MaxChannels : value);
                if (v == _channelCount) return;
                _channelCount = v;
                InvalidateVisual();
            }
        }

        private static readonly Brush BgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x3E)));
        private static readonly Pen BorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x72)), 1));
        private static readonly Pen PeakPen = Freeze(new Pen(Brushes.White, 1));
        private static readonly Pen ScalePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x85)), 1));
        private static readonly Brush ScaleTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0xA0)));

        // 色档停在 ClipLevel.WarnRatio（−1 dBFS）的橙上，与波形 near-clip 预警线对齐。
        // 渐变终点（1.0）停在橙，0 dB 以上那一段（+6 dB 余量区）另用橙红画，
        // 这样「已经到满幅」和「已经超出去」在颜色上一眼能分开。
        private static readonly GradientStopCollection Stops = Freeze(new GradientStopCollection
        {
            new GradientStop(Color.FromRgb(0x3D, 0xD5, 0x98), 0.000),              // 绿
            new GradientStop(Color.FromRgb(0x8F, 0xD5, 0x3D), 0.450),              // 黄绿
            new GradientStop(Color.FromRgb(0xD8, 0xD0, 0x35), 0.680),              // 黄
            new GradientStop(Color.FromRgb(0xFF, 0xC1, 0x3D), 0.820),              // 琥珀
            new GradientStop(Color.FromRgb(0xFF, 0x8A, 0x2B), ClipLevel.WarnRatio), // 橙 = −1 dBFS 预警起点
            new GradientStop(Color.FromRgb(0xFF, 0x8A, 0x2B), 1.000)               // 橙 = 满幅（0 dB 线）
        });

        /// <summary>0 dB 以上那一段（+6 dB 余量区）的颜色</summary>
        private static readonly Brush OverBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x3D)));

        private static readonly Brush FillBrushH = Freeze(new LinearGradientBrush(
            Stops, new Point(0, 0), new Point(1, 0)));
        private static readonly Brush FillBrushV = Freeze(new LinearGradientBrush(
            Stops, new Point(0, 1), new Point(0, 0)));

        private static readonly Dictionary<string, FormattedText> TextCache =
            new Dictionary<string, FormattedText>();

        private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        /// <summary>设置满格（0 dBFS）对应的振幅值。</summary>
        public void SetFullScale(double fullScale)
        {
            if (!(fullScale > 0)) return;
            if (Math.Abs(fullScale - _fullScale) < 1e-12) return;
            _fullScale = fullScale;
            InvalidateVisual();
        }

        /// <summary>改了显示范围后重算归零阈值（只在滚轮/复位时调用，不在绘制路径上）。</summary>
        private void UpdateFloorCache()
        {
            _zeroFloor = (float)Math.Pow(10.0, (_dbFloor - 6.0) / 20.0);
        }

        /// <summary>单声道快捷入口：两个声道给同一个值。</summary>
        public void SetLevel(float level)
        {
            SetLevels(new[] { level, level });
        }

        public void SetLevels(float[] levels)
        {
            long now = Stopwatch.GetTimestamp();

            double rawDt = _lastTick == 0
                ? 1.0 / 60.0
                : (now - _lastTick) / (double)Stopwatch.Frequency;

            // 界面被卡住或窗口最小化过，刚回来的这一帧不按真实间隔补算，
            // 否则电平和峰值会「噗通」掉一大截
            double dt = rawDt > 0.25 ? 1.0 / 60.0 : rawDt;
            _lastTick = now;

            bool changed = false;

            for (int i = 0; i < MaxChannels; i++)
            {
                float level = (levels != null && i < levels.Length) ? levels[i] : 0f;

                // 驱动异常时可能吐出 NaN / ±Inf，混进来会让峰值永久钉死在满格
                if (float.IsNaN(level) || float.IsInfinity(level)) level = 0f;
                if (level < 0f) level = 0f;

                float prevLevel = _level[i], prevPeak = _peak[i];

                // 快升慢降（attack / release）
                if (level >= _level[i]) _level[i] = level;
                else _level[i] += (level - _level[i]) * (float)(1.0 - Math.Exp(-dt / ReleaseTau));
                if (_level[i] < _zeroFloor) _level[i] = 0f;

                // 峰值保持 2 秒，到点之后才开始缓慢下落。
                // 判据必须严格大于：用 >= 会让 peak 落到当前电平被夹住后无限续命，
                // 表现成「冻结 2 秒、掉一下、再冻结 2 秒」的台阶。
                if (level > _peak[i] + 1e-6f)
                {
                    _peak[i] = level;
                    _peakHoldUntil[i] = now + (long)(Stopwatch.Frequency * PeakHoldSeconds);
                }
                else if (now > _peakHoldUntil[i])
                {
                    _peak[i] -= (float)(PeakFallPerSecond * _fullScale * dt);
                    if (_peak[i] < level) _peak[i] = level;
                }
                if (_peak[i] < 0f) _peak[i] = 0f;

                if (Math.Abs(_level[i] - prevLevel) > 1e-6f || Math.Abs(_peak[i] - prevPeak) > 1e-6f)
                    changed = true;
            }

            if (changed) InvalidateVisual();
        }

        /// <summary>换设备 / 重新开始时复位，连峰值保持计时一起清掉。</summary>
        public void Reset()
        {
            for (int i = 0; i < MaxChannels; i++)
            {
                _level[i] = 0f;
                _peak[i] = 0f;
                _peakHoldUntil[i] = 0;
            }
            _lastTick = 0;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            try { _lastDpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
            if (_lastDpi <= 0) _lastDpi = 1.0;

            if (IsVertical) RenderVertical(dc, w, h);
            else RenderHorizontal(dc, w, h);
        }

        /// <summary>顶部余量高度：始终固定在条的顶端，不随后续缩放改变</summary>
        private static double HeadroomOf(double h)
        {
            double ring = h * HeadroomFraction;
            if (ring < HeadroomMinPx && h * HeadroomMaxFraction > HeadroomMinPx) ring = HeadroomMinPx;
            if (ring > h * HeadroomMaxFraction) ring = h * HeadroomMaxFraction;
            return ring;
        }

        /// <summary>
        /// 电平 → 条内 y 坐标。
        /// 0 dB 在 ring，底部是 _dbFloor，中间按 dB 等距铺开 ——
        /// 这样 −6/−12/−24/−48 才能均匀标出来，而不是全挤在底部。
        /// 超过满幅的部分进顶部 +6 dB 余量区。
        /// </summary>
        private double LevelToY(double value, double h, double ring)
        {
            if (value <= 0) return h;

            double span = Math.Max(1.0, h - ring);

            if (value <= 1.0)
            {
                double db = 20.0 * Math.Log10(value);
                if (db <= _dbFloor) return h;
                return ring + (db / _dbFloor) * span;
            }

            return ring - Math.Min(value - 1.0, 1.0) * ring;
        }

        private void RenderVertical(DrawingContext dc, double w, double h)
        {
            double barX = ShowScale ? ScaleWidth : 0;
            double totalW = Math.Max(4, w - barX);
            double ring = HeadroomOf(h);

            int n = _channelCount;
            double gap = n > 1 ? ChannelGap : 0;
            double oneW = Math.Max(2, (totalW - gap * (n - 1)) / n);

            for (int i = 0; i < n; i++)
            {
                double x = barX + i * (oneW + gap);
                DrawBar(dc, x, oneW, h, ring, i);
            }

            if (ShowScale) DrawVerticalScale(dc, h, ring, barX);
        }

        private void DrawBar(DrawingContext dc, double x, double w, double h, double ring, int ch)
        {
            var bg = new Rect(x + 0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1));
            dc.DrawRoundedRectangle(BgBrush, BorderPen, bg, 4, 4);

            double lv = _fullScale > 0 ? _level[ch] / _fullScale : 0;
            if (lv > 0)
            {
                double yTop = LevelToY(lv, h, ring);
                if (yTop < h - 2)
                {
                    // 渐变按「0 dB 以下那一段」铺满，再用裁剪露出填充到的高度 ——
                    // 若直接把矩形画成填充高，WPF 会把整条渐变压扁进去，颜色就不表示响度了
                    dc.PushClip(new RectangleGeometry(
                        new Rect(x + 0.5, yTop, Math.Max(0, w - 1), h - yTop), 4, 4));
                    dc.DrawRectangle(FillBrushV, null,
                        new Rect(x + 1, ring, Math.Max(1, w - 2), Math.Max(1, h - ring)));

                    // 冲出 0 dB 的那一截单独上橙红，和「正好到满幅」区分开
                    if (yTop < ring)
                    {
                        dc.DrawRectangle(OverBrush, null,
                            new Rect(x + 1, yTop, Math.Max(1, w - 2), ring - yTop));
                    }
                    dc.Pop();
                }
            }

            double pv = _fullScale > 0 ? _peak[ch] / _fullScale : 0;
            if (pv > _zeroFloor)
            {
                double py = LevelToY(pv, h, ring);
                if (py < 1) py = 1;
                dc.DrawLine(PeakPen, new Point(x + 1, py), new Point(x + w - 1, py));
            }
        }

        private void DrawVerticalScale(DrawingContext dc, double h, double ring, double barX)
        {
            // 0 dB 在 ring，底部是 _dbFloor，中间按 dB 等距 ——
            // 和填充用的是同一套映射，所以标出来的就是真位置。
            double span = Math.Max(1.0, h - ring);

            // +6 dB 正好是条的顶沿；往下让几像素，否则文字会贴边被裁掉
            DrawScaleTick(dc, barX, 6, "+6");

            double lastY = double.MinValue;
            for (double db = 0; db >= _dbFloor - 0.5; db -= 6)
            {
                double y = ring + (db / _dbFloor) * span;
                // 最低那一档贴到底部也要画出来，否则看的人会以为下面是 −∞
                if (y > h - 1) y = h - 1;
                // 和上一条贴太近就跳过，免得糊成一团看不清
                if (y - lastY < 11) continue;
                DrawScaleTick(dc, barX, y, db.ToString("0"));
                lastY = y;
            }
        }

        /// <summary>双击复位刻度范围（回到默认显示下限）。</summary>
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                if (Math.Abs(_dbFloor - DefaultDbFloor) > 1e-9)
                {
                    _dbFloor = DefaultDbFloor;
                    UpdateFloorCache();
                    InvalidateVisual();
                }
                e.Handled = true;
                return;
            }
            base.OnMouseLeftButtonDown(e);
        }

        /// <summary>
        /// 滚轮调节刻度显示下限：每滚一格固定 6 dB。
        /// 不能用「乘一个系数再量化」——那样会出现「滚一下没变化」，
        /// 而且量化后碰到边界会卡死（缩到 −12 就再也滚不回来）。
        /// </summary>
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            double floor = _dbFloor + (e.Delta > 0 ? 6.0 : -6.0);

            if (floor < MinDbFloor) floor = MinDbFloor;
            if (floor > MaxDbFloor) floor = MaxDbFloor;

            if (Math.Abs(floor - _dbFloor) > 1e-9)
            {
                _dbFloor = floor;
                UpdateFloorCache();
                InvalidateVisual();
            }
            e.Handled = true;
        }

        private void DrawScaleTick(DrawingContext dc, double barX, double y, string label)
        {
            if (y < 1 || y > ActualHeight - 1) return;
            dc.DrawLine(ScalePen, new Point(barX - 4, y), new Point(barX, y));

            var ft = GetText(label);
            double tx = barX - 6 - ft.Width;
            if (tx < 0) tx = 0;
            dc.DrawText(ft, new Point(tx, y - ft.Height / 2));
        }

        private FormattedText GetText(string s)
        {
            FormattedText ft;
            if (TextCache.TryGetValue(s, out ft)) return ft;

            ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 9, ScaleTextBrush, _lastDpi);
            TextCache[s] = ft;
            return ft;
        }

        private void RenderHorizontal(DrawingContext dc, double w, double h)
        {
            int n = _channelCount;
            double gap = n > 1 ? ChannelGap : 0;
            double oneH = Math.Max(2, (h - gap * (n - 1)) / n);

            for (int i = 0; i < n; i++)
                DrawBarHorizontal(dc, i * (oneH + gap), oneH, w, i);
        }

        private void DrawBarHorizontal(DrawingContext dc, double y, double barH, double w, int ch)
        {
            var bg = new Rect(0.5, y + 0.5, Math.Max(0, w - 1), Math.Max(0, barH - 1));
            dc.DrawRoundedRectangle(BgBrush, BorderPen, bg, 4, 4);

            double lv = _fullScale > 0 ? _level[ch] / _fullScale : 0;
            if (lv > 1) lv = 1;
            if (lv < 0) lv = 0;

            double fw = Math.Max(0, w * lv - 1);
            if (fw > 1)
            {
                dc.PushClip(new RectangleGeometry(new Rect(0.5, y + 0.5, fw, Math.Max(0, barH - 1)), 4, 4));
                dc.DrawRectangle(FillBrushH, null,
                    new Rect(1, y + 1, Math.Max(0, w - 2), Math.Max(1, barH - 2)));
                dc.Pop();
            }

            double pv = _fullScale > 0 ? _peak[ch] / _fullScale : 0;
            if (pv > 1) pv = 1;
            if (pv > 0.01)
            {
                double px = w * pv;
                dc.DrawLine(PeakPen, new Point(px, y), new Point(px, y + barH));
            }
        }
    }
}
