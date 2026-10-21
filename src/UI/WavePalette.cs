using System.Windows.Media;

namespace RecLite
{
    /// <summary>
    /// 时间轴和秒数指示器共用的配色。
    ///
    /// 这两处表达的是同一件事 ——「预录缓存了多久 / 正式录了多久」——
    /// 颜色必须保持一致，所以抽成一份，改这里两边同时生效。
    /// </summary>
    public static class WavePalette
    {
        /// <summary>预录段：绿。数据只进了缓存，还没正式写进文件。</summary>
        public static readonly Color Preroll = Color.FromRgb(0x3D, 0xD5, 0x98);

        /// <summary>录制段：黄。已经在写文件了。</summary>
        public static readonly Color Recording = Color.FromRgb(0xE8, 0xC4, 0x4A);

        /// <summary>空闲：白。什么都没在跑。</summary>
        public static readonly Color Idle = Color.FromRgb(0xD8, 0xD8, 0xE8);

        /// <summary>时间轴的预录色带（半透明，垫在波形下方不抢眼）</summary>
        public static readonly Brush PrerollBand = Make(Preroll, 0x66);

        /// <summary>时间轴的录制色带（半透明）</summary>
        public static readonly Brush RecordingBand = Make(Recording, 0x55);

        /// <summary>秒数指示器：预录中（不透明，要看得清）</summary>
        public static readonly Brush PrerollText = Make(Preroll, 0xFF);

        /// <summary>秒数指示器：录制中（不透明）</summary>
        public static readonly Brush RecordingText = Make(Recording, 0xFF);

        /// <summary>秒数指示器：空闲（不透明）</summary>
        public static readonly Brush IdleText = Make(Idle, 0xFF);

        private static Brush Make(Color c, byte alpha)
        {
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
    }
}
