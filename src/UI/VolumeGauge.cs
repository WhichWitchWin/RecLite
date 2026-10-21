using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RecLite
{
    /// <summary>
    /// 音量数字控件（替代原来的「滑条 + 百分数」）。
    ///
    /// 平时只显示一个纯数字（不带 %）；鼠标移上去在右侧弹出滑条。
    /// 四种调法都支持：
    ///   · 拖动弹出的滑条
    ///   · 在数字上滚动滚轮（每档 2）
    ///   · 在数字上按住上下拖动（往上拖变大）
    ///   · 点一下数字直接键入
    /// 双击数字进入编辑，回车或点别处提交（和预录秒数栏一致）。
    /// 归位 100% 只保留在滑条上双击。
    /// 上限由 MaxPercent 决定，鼠标悬停时会提示范围。
    /// </summary>
    public class VolumeGauge : Grid
    {
        private readonly TextBlock _text = new TextBlock();
        private readonly TextBox _edit = new TextBox();
        private readonly Popup _popup = new Popup();
        private readonly Slider _slider = new Slider();
        private readonly DispatcherTimer _closeTimer = new DispatcherTimer();

        private double _value = 1.0;
        private bool _dragging;
        private bool _moved;
        private double _dragStartY;
        private double _dragStartValue;
        private bool _suppress;

        /// <summary>上限（百分比）：录制增益到 200，试听到 100。</summary>
        public double MaxPercent { get; set; }

        public event EventHandler ValueChanged;

        public double Value
        {
            get { return _value; }
            set { ApplyValue(value, true); }
        }

        public VolumeGauge()
        {
            Width = 48;
            Height = 26;
            Cursor = Cursors.SizeNS;            // 提示可以按住上下拖
            Background = Brushes.Transparent;   // 透明也要能接住鼠标

            _text.HorizontalAlignment = HorizontalAlignment.Center;
            _text.VerticalAlignment = VerticalAlignment.Center;
            _text.FontFamily = new FontFamily("Consolas");
            _text.FontSize = 12;
            _text.IsHitTestVisible = false;
            Children.Add(_text);

            _edit.Visibility = Visibility.Collapsed;
            _edit.HorizontalContentAlignment = HorizontalAlignment.Center;
            _edit.FontFamily = new FontFamily("Consolas");
            _edit.FontSize = 12;
            _edit.BorderThickness = new Thickness(0, 0, 0, 1);
            _edit.Background = Brushes.Transparent;
            _edit.Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xE8));
            _edit.KeyDown += Edit_KeyDown;
            _edit.LostFocus += (s, e) => CommitEdit();
            Children.Add(_edit);

            _slider.Minimum = 0;
            _slider.Maximum = 2;
            _slider.Orientation = Orientation.Vertical;
            // 宽度只比拇指略宽就够了。
            // 原来设 30：竖直 Slider 的默认模板里「轨道」不会撑满控件宽度，
            // 控件比轨道宽出来的一截会全落在一边，看起来就是滑条歪在框里偏左。
            // 现在控件几乎贴着轨道宽度，再显式居中，就一定在正中间。
            _slider.Width = 16;
            _slider.Height = 100;
            _slider.IsMoveToPointEnabled = true;
            _slider.HorizontalAlignment = HorizontalAlignment.Center;
            _slider.Margin = new Thickness(0);

            // 默认模板里 Track / RepeatButton 的横向对齐带一点固定偏移 ——
            // 实测「拇指看着居中、整条滑轨却偏右」，改宽度只会让偏向左右乱跳（30 时偏左、16 时偏右）。
            // 自己画模板：滑轨和拇指都用 HorizontalAlignment=Center，位置就是确定的居中。
            _slider.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                 TargetType='Slider'>
  <Grid Background='Transparent'>
    <Border Width='5' HorizontalAlignment='Center' CornerRadius='2' Background='#3A3A48'/>
    <Track x:Name='PART_Track' Orientation='Vertical'>
      <Track.DecreaseRepeatButton>
        <RepeatButton Command='{x:Static Slider.DecreaseLarge}' Opacity='0' Focusable='False'/>
      </Track.DecreaseRepeatButton>
      <Track.Thumb>
        <Thumb Width='13' Height='15' HorizontalAlignment='Center'>
          <Thumb.Template>
            <ControlTemplate TargetType='Thumb'>
              <Border Background='#E9E9F2' CornerRadius='3'/>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
      <Track.IncreaseRepeatButton>
        <RepeatButton Command='{x:Static Slider.IncreaseLarge}' Opacity='0' Focusable='False'/>
      </Track.IncreaseRepeatButton>
    </Track>
  </Grid>
</ControlTemplate>");
            _slider.ValueChanged += Slider_ValueChanged;

            // 在滑条上滚轮也能调音量（和数字上一致，每档 2）
            _slider.PreviewMouseWheel += (s, e) =>
            {
                ApplyValue(_value + (e.Delta > 0 ? 0.02 : -0.02), true);
                e.Handled = true;
            };

            // 双击滑块归位 100%（只在双击时吃掉事件，单击照常拖拽）
            _slider.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount != 2) return;
                ApplyValue(1.0, true);
                e.Handled = true;
            };

            _popup.Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x33)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x72)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 6, 9, 6),   // 留白交给 Border，滑条自己不带 margin，才能保证居中
                Child = _slider
            };
            _popup.PlacementTarget = this;
            _popup.Placement = PlacementMode.Right;
            _popup.AllowsTransparency = true;
            _popup.StaysOpen = true;
            _popup.HorizontalOffset = 1;   // 原来 4：离数字太远，贴近一点

            _closeTimer.Interval = TimeSpan.FromMilliseconds(220);
            _closeTimer.Tick += (s, e) =>
            {
                _closeTimer.Stop();
                if (!IsMouseOver && !_popup.IsMouseOver) _popup.IsOpen = false;
            };

            MouseEnter += (s, e) => { _closeTimer.Stop(); _popup.IsOpen = true; };
            MouseLeave += (s, e) => _closeTimer.Start();
            _popup.MouseEnter += (s, e) => _closeTimer.Stop();
            _popup.MouseLeave += (s, e) => _closeTimer.Start();

            MouseWheel += Gauge_MouseWheel;
            MouseLeftButtonDown += Gauge_MouseDown;
            MouseMove += Gauge_MouseMove;
            MouseLeftButtonUp += Gauge_MouseUp;

            Loaded += (s, e) =>
            {
                ToolTip = string.Format("范围 0 ~ {0}　滑条 / 滚轮 / 上下拖 / 双击数字输入（滑条上双击归位 100）",
                    (int)MaxPercent);
            };

            UpdateText();
        }

        private double MaxValue { get { return MaxPercent / 100.0; } }

        private void ApplyValue(double v, bool raise)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return;

            if (v < 0) v = 0;
            if (v > MaxValue) v = MaxValue;

            bool changed = Math.Abs(v - _value) > 1e-9;
            _value = v;

            _suppress = true;
            _slider.Maximum = MaxValue;
            _slider.Value = v;
            _suppress = false;

            UpdateText();
            if (changed && raise && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        private void UpdateText()
        {
            if (_edit.Visibility == Visibility.Visible) return;
            _text.Text = ((int)Math.Round(_value * 100)).ToString(CultureInfo.InvariantCulture);
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppress) return;
            ApplyValue(e.NewValue, true);
        }

        private void Gauge_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ApplyValue(_value + (e.Delta > 0 ? 0.02 : -0.02), true);
            e.Handled = true;
        }

        /// <summary>
        /// 若正在编辑就提交并退出。窗口级的 PreviewMouseDown 会调这个，
        /// 这样点任何空白处都能退出编辑（点编辑框自己不算）。
        /// </summary>
        public void CommitIfEditing()
        {
            if (_edit.Visibility != Visibility.Visible) return;
            if (_edit.IsMouseOver) return;      // 点在输入框里面，继续编辑
            CommitEdit();
        }

        private void Gauge_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // 编辑状态下窗口级 PreviewMouseDown 已经先提交了，这里只要别继续走拖拽逻辑
            if (_edit.Visibility == Visibility.Visible) return;

            // 双击 = 进入编辑（和预录秒数栏一致：回车或点别处提交）
            if (e.ClickCount == 2)
            {
                _dragging = false;
                ReleaseMouseCapture();
                BeginEdit();
                e.Handled = true;
                return;
            }

            _dragging = true;
            _moved = false;
            _dragStartY = e.GetPosition(this).Y;
            _dragStartValue = _value;
            CaptureMouse();
            e.Handled = true;
        }

        private void Gauge_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            // 往上拖 = 变大；拖满 100 px 走满一个量程
            double dy = _dragStartY - e.GetPosition(this).Y;
            if (Math.Abs(dy) > 2) _moved = true;
            if (_moved) ApplyValue(_dragStartValue + dy * 0.01, true);
        }

        private void Gauge_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;

            _dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        private void BeginEdit()
        {
            _text.Visibility = Visibility.Collapsed;
            _edit.Visibility = Visibility.Visible;
            _edit.Text = ((int)Math.Round(_value * 100)).ToString(CultureInfo.InvariantCulture);
            _edit.SelectAll();
            _edit.Focus();
        }

        private void Edit_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitEdit(); e.Handled = true; }
            else if (e.Key == Key.Escape)
            {
                _edit.Visibility = Visibility.Collapsed;
                _text.Visibility = Visibility.Visible;
                e.Handled = true;
            }
        }

        private void CommitEdit()
        {
            if (_edit.Visibility != Visibility.Visible) return;

            double pct;
            if (double.TryParse(_edit.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out pct)
                && !double.IsNaN(pct) && !double.IsInfinity(pct))
            {
                ApplyValue(pct / 100.0, true);
            }

            _edit.Visibility = Visibility.Collapsed;
            _text.Visibility = Visibility.Visible;
            UpdateText();
        }
    }
}
