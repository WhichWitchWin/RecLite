using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RecLite
{
    /// <summary>
    /// 削波指示灯（单声道一个灯，双声道左右两个灯）。
    ///
    /// 波形上的红色是「即时」的 —— 只覆盖真正过载的那一小段采样，
    /// 声音短的时候一闪就过去了，人眼根本抓不到。所以真要确认有没有爆，
    /// 得靠这个灯：一旦削过就常亮，直到用户自己点一下复位。
    /// 双声道时点任意一个都算复位（左右一起清），因为「这一条录音爆过没有」是整体判断。
    /// </summary>
    public class ClipIndicator : FrameworkElement
    {
        private const int MaxChannels = 2;
        private const double Gap = 2;

        private readonly bool[] _lit = new bool[MaxChannels];
        private int _channelCount = 1;

        /// <summary>显示几个灯（1 或 2）</summary>
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

        private static readonly Brush OffBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x4A)));
        private static readonly Brush OnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x45)));
        private static readonly Pen OffPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x72)), 1));
        private static readonly Pen OnPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x9A)), 1));

        private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        public ClipIndicator()
        {
            ToolTip = "削波指示：过载后常亮，点一下左右一起复位";
            Cursor = Cursors.Hand;
        }

        /// <summary>亮起全声道（单声道时等同于 SetClipped(0)）</summary>
        public void SetClipped()
        {
            SetClipped(0);
            SetClipped(1);
        }

        /// <summary>只亮某个声道；已亮时不做任何事，避免每帧重复重绘。</summary>
        public void SetClipped(int channel)
        {
            if (channel < 0 || channel >= MaxChannels) return;
            if (_lit[channel]) return;
            _lit[channel] = true;
            InvalidateVisual();
        }

        /// <summary>左右一起复位（点一下灯、或开始新录音时主动清）。</summary>
        public void Reset()
        {
            if (!_lit[0] && !_lit[1]) return;
            _lit[0] = false;
            _lit[1] = false;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Reset();
            e.Handled = true;
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            // 默认只按「绘制出来的内容」命中，圆角四个角和描边那半像素是死区。
            // 这里直接吃满整个控件矩形，保证任意位置都能点灭。
            return new PointHitTestResult(this, hitTestParameters.HitPoint);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            int n = _channelCount;
            double oneW = Math.Max(1, (w - Gap * (n - 1)) / n);

            for (int i = 0; i < n; i++)
            {
                double x = i * (oneW + Gap);
                // 留半像素给描边，否则圆角那一圈会被裁掉
                var box = new Rect(x + 0.5, 0.5, Math.Max(0, oneW - 1), Math.Max(0, h - 1));
                bool on = _lit[i];
                dc.DrawRoundedRectangle(on ? OnBrush : OffBrush, on ? OnPen : OffPen, box, 1.5, 1.5);
            }
        }
    }
}
