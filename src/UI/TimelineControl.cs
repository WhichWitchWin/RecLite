using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RecLite
{
    /// <summary>
    /// 波形下方的时间轴。
    ///
    /// 为什么单独做成一个控件而不是画在波形里：
    /// 画在波形内部需要在顶部切掉一条，那样所有纵轴坐标（波形、参考线、刻度、播放头、
    /// 点击定位）都要跟着重算，风险大；单独一条只碰横轴，波形控件完全不用改。
    ///
    /// 双击这里 = 复位缩放（波形区的双击已经取消，避免和单击定位抢手势）。
    /// </summary>
    public class TimelineControl : FrameworkElement
    {
        private static readonly Brush BgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x10)));
        private static readonly Brush TickBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x5E)));
        private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xC0)));
        private static readonly Pen TickPen = Freeze(new Pen(TickBrush, 1));

        private static readonly Typeface Face = new Typeface("Consolas");

        private WaveformControl _wf;

        /// <summary>当前波形数据的时长（秒）。null = 没有在预录/录制，时间轴保持静态。</summary>
        private double? _curTime;

        /// <summary>预录时长（秒）。0 表示没开预录。</summary>
        private double _preroll;

        // 色带颜色统一从 WavePalette 取：秒数指示器用的是同一套颜色，
        // 两处必须一致，改配色只改一处。
        private static readonly Brush PrerollBrush = WavePalette.PrerollBand;
        private static readonly Brush RecBrush = WavePalette.RecordingBand;

        /// <summary>
        /// 由 MainWindow 每帧设置：
        ///   curSeconds = 预录 + 已录的总秒数；null 表示没在预录/录制（时间轴转为静态刻度）。
        /// 0 秒永远表示「预录起点」，所以绿段从 0 到 preroll，黄段从 preroll 往后。
        /// </summary>
        public void SetTime(double? curSeconds, double prerollSeconds)
        {
            _curTime = curSeconds;
            _preroll = prerollSeconds < 0 ? 0 : prerollSeconds;
        }

        public void Attach(WaveformControl wf)
        {
            _wf = wf;
            InvalidateVisual();
        }

        private static T Freeze<T>(T f) where T : Freezable
        {
            if (f != null) f.Freeze();
            return f;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            // 双击时间轴 = 只复位「横轴」（X），纵轴缩放不动
            if (e.ClickCount == 2)
            {
                if (_wf != null) _wf.ResetZoomX();
                e.Handled = true;
            }
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
            if (_wf == null) return;

            // 算出当前可见区间对应的时间范围（秒）
            double t0, t1;
            double ppp = _wf.PixelsPerPointNow;
            if (ppp <= 0) return;

            if (_wf.IsStaticMode)
            {
                // 试听：整首文件的固定区间
                int total = _wf.StaticTotalPoints;
                double dur = _wf.StaticDurationSeconds;
                if (total <= 0 || dur <= 0) return;

                double start = _wf.StaticStart;
                double visible = Math.Min(total, Math.Max(1, w / ppp));

                t0 = dur * (start / total);
                t1 = dur * Math.Min(1.0, (start + visible) / total);
            }
            else
            {
                double pps = _wf.PointsPerSecondNow;
                if (pps <= 0) return;

                double span = (w / ppp) / pps;   // 整屏能装多少秒

                if (_curTime.HasValue)
                {
                    // 预录/录制中：右侧是「现在」，整条跟着时间往左走。
                    // 0 秒 = 预录起点，因此 t 为负表示预录起点之前的空白。
                    t1 = _curTime.Value;
                    t0 = t1 - span;
                }
                else
                {
                    // 待机且没开预录：静态刻度，只表达「这个界面宽度能容纳多长时间」
                    t0 = 0;
                    t1 = span;
                }
            }

            if (t1 <= t0) return;

            // 染色：绿色 = 预录段（0 ~ preroll），黄色 = 录制段（preroll ~ 现在）
            if (_curTime.HasValue)
            {
                double recEnd = Math.Min(t1, _curTime.Value);

                double preStart = Math.Max(t0, 0);
                double preEnd = Math.Min(recEnd, _preroll);
                if (preEnd > preStart)
                {
                    double xa = TimeToX(preStart, t0, t1, w);
                    double xb = TimeToX(preEnd, t0, t1, w);
                    dc.DrawRectangle(PrerollBrush, null, new Rect(xa, 0, Math.Max(0, xb - xa), h));
                }

                double rStart = Math.Max(t0, _preroll);
                double rEnd = recEnd;
                if (rEnd > rStart)
                {
                    double xa = TimeToX(rStart, t0, t1, w);
                    double xb = TimeToX(rEnd, t0, t1, w);
                    dc.DrawRectangle(RecBrush, null, new Rect(xa, 0, Math.Max(0, xb - xa), h));
                }
            }

            // 刻度步长自适应
            double step = PickStep(t1 - t0);
            double first = Math.Ceiling(t0 / step) * step;

            var ft = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Face, 10, TextBrush, 1.0);

            for (double t = first; t <= t1 + 1e-9; t += step)
            {
                double x = TimeToX(t, t0, t1, w);
                if (x < 0 || x > w) continue;

                dc.DrawLine(TickPen, new Point(x, h - 6), new Point(x, h));

                string label = FormatTime(t, step);
                ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    Face, 10, TextBrush, 1.0);
                double tx = x + 3;
                if (tx + ft.Width > w) tx = x - ft.Width - 3;
                dc.DrawText(ft, new Point(tx, 1));
            }
        }

        private static double TimeToX(double t, double t0, double t1, double w)
        {
            return (t - t0) / (t1 - t0) * w;
        }

        /// <summary>按可见时长挑一个「整」步长，避免刻度挤成一片或只剩一两个。</summary>
        private static double PickStep(double spanSeconds)
        {
            double[] steps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };
            foreach (var s in steps)
                if (spanSeconds / s <= 8) return s;
            return 3600;
        }

        private static string FormatTime(double t, double step)
        {
            bool neg = t < 0;
            double a = Math.Abs(t);

            string s;
            if (step < 1)
                s = a.ToString("0.0", CultureInfo.InvariantCulture) + "s";
            else if (a < 60)
                s = ((int)Math.Round(a)).ToString(CultureInfo.InvariantCulture) + "s";
            else
            {
                int mm = (int)(a / 60);
                int ss = (int)Math.Round(a % 60);
                if (ss == 60) { mm++; ss = 0; }
                s = string.Format("{0}:{1:00}", mm, ss);
            }
            return neg ? "-" + s : s;
        }
    }
}
