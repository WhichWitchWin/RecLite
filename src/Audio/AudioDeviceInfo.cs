using System;

namespace RecLite.Audio
{
    /// <summary>音频设备类型</summary>
    public enum DeviceKind
    {
        /// <summary>录音设备（麦克风 / 声卡输入，WASAPI Capture）</summary>
        Capture = 0,
        /// <summary>播放设备的回环（录电脑正在播放的声音，WASAPI Loopback）</summary>
        Loopback = 1,
        /// <summary>纯播放设备（只用于试听，不作为录制源）</summary>
        Render = 2
    }

    /// <summary>
    /// 一个音频设备的快照信息。只保存值，不持有 COM 对象，避免设备热插拔后引用失效。
    /// </summary>
    public class AudioDeviceInfo
    {
        /// <summary>WASAPI 端点 ID（稳定标识）</summary>
        public string Id { get; set; } = "";

        /// <summary>设备友好名称</summary>
        public string Name { get; set; } = "";

        /// <summary>设备类别</summary>
        public DeviceKind Kind { get; set; }

        /// <summary>设备混音格式：采样率</summary>
        public int SampleRate { get; set; }

        /// <summary>设备混音格式：声道数</summary>
        public int Channels { get; set; }

        /// <summary>设备混音格式：位深（32 表示 32bit float）</summary>
        public int BitsPerSample { get; set; }

        /// <summary>下拉框显示文本</summary>
        public string DisplayName
        {
            get
            {
                var suffix = Kind == DeviceKind.Loopback ? "  〔内录〕" : "";
                return string.Format("{0}{1}", Name, suffix);
            }
        }

        /// <summary>格式摘要，如 "48000Hz · 2ch · 32bit"</summary>
        public string FormatSummary
        {
            get
            {
                if (SampleRate <= 0) return "";
                return string.Format("{0} Hz · {1} 声道 · {2} bit", SampleRate, Channels, BitsPerSample);
            }
        }

        public override string ToString() => DisplayName;
    }
}
