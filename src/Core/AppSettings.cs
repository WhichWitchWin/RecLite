using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RecLite.Core
{
    /// <summary>
    /// 应用配置。保存在程序目录的 settings.json —— 绿色化，不写注册表、不写 AppData。
    /// 用 DataContractJsonSerializer（.NET Framework 自带，零第三方依赖）。
    /// </summary>
    [DataContract]
    public class AppSettings
    {
        /// <summary>录音设备 ID（MMDevice.Id）；空 = 系统默认录音设备</summary>
        [DataMember] public string CaptureDeviceId { get; set; }

        /// <summary>true = 从「播放设备」取回环（录电脑声）；false = 直接采集录音设备</summary>
        [DataMember] public bool CaptureFromRenderLoopback { get; set; }

        /// <summary>试听播放设备 ID；空 = 系统默认播放设备</summary>
        [DataMember] public string PlaybackDeviceId { get; set; }

        /// <summary>输出格式：wav / mp3</summary>
        [DataMember] public string OutputFormat { get; set; }

        /// <summary>目标采样率；0 = 跟随设备原始参数</summary>
        [DataMember] public int SampleRate { get; set; }

        /// <summary>WAV 位深：16 / 24 / 32(float)；0 = 跟随设备</summary>
        [DataMember] public int BitsPerSample { get; set; }

        /// <summary>MP3 码率 kbps：128 / 192 / 256 / 320</summary>
        [DataMember] public int Mp3Bitrate { get; set; }

        /// <summary>预录缓存秒数（0 = 关闭预录）。全新安装的默认值见 DefaultPrerollSeconds</summary>
        [DataMember] public int PrerollSeconds { get; set; }

        /// <summary>旧版字段：预录分钟数（仅用于兼容读取旧配置）</summary>
        [DataMember] public int PrerollMinutes { get; set; }

        /// <summary>文件名模式：auto = 自动时间戳；ask = 停止后弹窗询问</summary>
        [DataMember] public string FileNameMode { get; set; }

        /// <summary>文件名前缀（auto 模式下使用）</summary>
        [DataMember] public string FileNamePrefix { get; set; }

        /// <summary>录音输出目录；空 = 程序目录下的 Recordings</summary>
        [DataMember] public string RecordingsDir { get; set; }

        /// <summary>UI 主题：dark / light</summary>
        [DataMember] public string Theme { get; set; }

        /// <summary>保存时「不」把预录片段接到文件开头（false = 包含预录，默认）</summary>
        [DataMember] public bool SkipPreroll { get; set; }

        /// <summary>待机（未录音、未预录）时「隐藏」波形</summary>
        [DataMember] public bool HideWaveformIdle { get; set; }

        /// <summary>上次关闭时的窗口位置与大小（NaN / 0 表示还没存过）</summary>
        [DataMember] public double WindowLeft { get; set; }
        [DataMember] public double WindowTop { get; set; }
        [DataMember] public double WindowWidth { get; set; }
        [DataMember] public double WindowHeight { get; set; }

        /// <summary>录制增益（软件内部，不改系统音量）。1.0 = 原样</summary>
        [DataMember] public double InputGain { get; set; }

        /// <summary>试听播放音量（软件内部，不改系统音量）。1.0 = 原样</summary>
        [DataMember] public double PlaybackVolume { get; set; }

        /// <summary>波形是否使用对数(dB)刻度显示</summary>
        [DataMember] public bool WaveLogScale { get; set; }

        // ---------- 默认值 ----------

        /// <summary>全新安装的预录缓存默认秒数（预录是本程序的核心功能，默认开）</summary>
        public const int DefaultPrerollSeconds = 15;

        public AppSettings()
        {
            SetDefaults();
        }

        /// <summary>
        /// 全新安装 / 配置文件缺失 / 配置文件损坏时使用的默认设置。
        /// 预录秒数必须在这里显式给值：DataContractJsonSerializer 不走构造函数，
        /// 反序列化得到的实例里 int 字段一律是 0，等于「预录关闭」。
        /// </summary>
        private static AppSettings CreateDefault()
        {
            var s = new AppSettings();
            s.PrerollSeconds = DefaultPrerollSeconds;
            return s;
        }

        private void SetDefaults()
        {
            if (CaptureDeviceId == null) CaptureDeviceId = "";
            if (PlaybackDeviceId == null) PlaybackDeviceId = "";
            if (OutputFormat == null) OutputFormat = "wav";
            // 默认跟随设备参数：设备是 32bit 浮点，就默认 32bit
            if (BitsPerSample <= 0) BitsPerSample = 32;
            if (Mp3Bitrate <= 0) Mp3Bitrate = 192;
            if (FileNameMode == null) FileNameMode = "auto";
            if (FileNamePrefix == null) FileNamePrefix = "录音";
            if (RecordingsDir == null) RecordingsDir = "";
            if (Theme == null) Theme = "dark";
            // 旧配置兼容：老版本只存了「分钟」，换算成秒
            if (PrerollSeconds <= 0 && PrerollMinutes > 0) PrerollSeconds = PrerollMinutes * 60;
            if (PrerollSeconds < 0) PrerollSeconds = 0;
            // 只拦「明显非法」的值。
            // 注意不能用 <= 0 —— 0 是合法值（用户可能就想静音），把它纠正成 1.0
            // 会让用户设的静音在重启后悄悄失效。
            // 「旧配置缺字段变成 0」的问题由上面的 OnDeserializing 兜住。
            if (double.IsNaN(InputGain) || InputGain < 0) InputGain = 1.0;
            if (double.IsNaN(PlaybackVolume) || PlaybackVolume < 0) PlaybackVolume = 1.0;
        }

        // ---------- 读写 ----------

        public static AppSettings Load()
        {
            try
            {
                var path = AppPaths.SettingsFile;
                if (!File.Exists(path)) return CreateDefault();

                AppSettings s;
                string json;
                using (var fs = File.OpenRead(path))
                {
                    var ser = new DataContractJsonSerializer(typeof(AppSettings));
                    s = ser.ReadObject(fs) as AppSettings;
                }
                if (s == null) return CreateDefault();

                // 【为什么不用 OnDeserializing】DataContractJsonSerializer 不走构造函数，
                // 缺字段会变成 0（增益 0 = 静音录制、预录 0 = 关闭），而
                // [OnDeserializing] 在这条链路上实测不生效。
                // 所以直接查原始 JSON：字段真的不存在才填默认值 ——
                // 这样用户明确设的 0（比如就想静音）不会被误改。
                json = File.ReadAllText(path, Encoding.UTF8);
                if (json.IndexOf("\"InputGain\"", StringComparison.Ordinal) < 0) s.InputGain = 1.0;
                if (json.IndexOf("\"PlaybackVolume\"", StringComparison.Ordinal) < 0) s.PlaybackVolume = 1.0;
                if (json.IndexOf("\"PrerollSeconds\"", StringComparison.Ordinal) < 0) s.PrerollSeconds = DefaultPrerollSeconds;

                s.SetDefaults();
                return s;
            }
            catch
            {
                // 配置损坏时回退到默认值，绝不让程序起不来
                return CreateDefault();
            }
        }

        public void Save()
        {
            try
            {
                AppPaths.EnsureDirectory(AppPaths.BaseDir);
                var ser = new DataContractJsonSerializer(typeof(AppSettings));
                using (var fs = File.Create(AppPaths.SettingsFile))
                {
                    ser.WriteObject(fs, this);
                }
            }
            catch
            {
                // 配置文件写入失败不影响录音
            }
        }

        /// <summary>解析出实际录音目录（绝对路径）</summary>
        public string GetRecordingsDir()
        {
            return string.IsNullOrWhiteSpace(RecordingsDir)
                ? AppPaths.DefaultRecordingsDir
                : AppPaths.Resolve(RecordingsDir);
        }
    }
}
