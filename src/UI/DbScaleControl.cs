using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RecLite
{
    /// <summary>
    /// 波形左侧的 dB 刻度条。
    ///
    /// 从波形内部搬出来的原因：刻度标签压在波形上，双击复位时点击区域和定位区域重合，
    /// 会先把播放位置跳过去再复位。独立成一条之后，点这里永远不会动播放位置。
    ///
    /// 位置全部由 WaveformControl.DbToY 提供，和波形绘制共用同一套映射，不会错位。
    /// </summary>
    public class DbScaleControl : FrameworkElement
    {
        private static readonly Brush BgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x10)));
        private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xDC)));

        private static readonly Typeface Face = new Typeface("Consolas");

        /// <summary>要标注的 dB 值（从大到小）。太密时下面的循环会自动跳过。</summary>
        private static readonly double[] Levels = { 0, -6, -12, -18, -24, -36 };

        private WaveformControl _wf;

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
            // 双击刻度条 = 只复位「纵轴」（Y），横向缩放不动
            if (e.ClickCount == 2)
            {
                if (_wf != null) _wf.ResetZoomY();
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

            int ch = _wf.ChannelCountNow;

            for (int c = 0; c < ch; c++)
            {
                double lastY = -100;
                foreach (var db in Levels)
                {
                    double y = _wf.DbToY(c, db);
                    if (y < 2 || y > h - 4) continue;
                    // 和上一条贴太近就跳过，免得糊成一团
                    if (y - lastY < 10) continue;
                    lastY = y;

                    string text = db > 0 ? "+" + db : db.ToString(CultureInfo.InvariantCulture);
                    var ft = new FormattedText(text, CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, Face, 10, TextBrush, 1.0);
                    // 右对齐贴着波形边缘
                    dc.DrawText(ft, new Point(Math.Max(1, w - ft.Width - 3), y - ft.Height / 2));
                }
            }
        }
    }
}
