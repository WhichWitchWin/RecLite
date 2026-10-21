using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RecLite.Audio;
using RecLite.Core;

namespace RecLite
{
    /// <summary>
    /// 无界面自检：验证设备枚举 → 打开设备 → 预录缓存 → 录制 → WAV 落盘 全链路。
    /// 用法：RecLite.exe --selftest &lt;日志路径&gt;
    /// </summary>
    public static class SelfTest
    {
        /// <summary>
        /// 把一个指定点数的波形渲染成 PNG，用于验证「固定像素密度」是否生效：
        /// 点数少时波形应贴在右边一小段，而不是被拉伸铺满整个宽度。
        /// </summary>
        private static void RenderWaveform(string outDir, int pointCount, bool logScale = false, double zoom = 1.0)
        {
            const int W = 800, H = 120;
            var wc = new WaveformControl { Width = W, Height = H };
            wc.Configure(1);
            wc.UseLogScale = logScale;

            var pts = new PeakPoint[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                // 固定满幅正弦，排除信号本身的影响
                double v = 0.85 * Math.Abs(Math.Sin(i * 0.35));
                pts[i] = new PeakPoint
                {
                    Min = new[] { (float)(-v) },
                    Max = new[] { (float)v }
                };
            }
            wc.SetStatic(pts, 1);
            if (zoom != 1.0) wc.ZoomBy(zoom);
            wc.Measure(new Size(W, H));
            wc.Arrange(new Rect(0, 0, W, H));
            wc.UpdateLayout();

            // 记录实际映射后的高度，便于定位
            double maxMapped = 0;
            for (int i = 0; i < pointCount; i++)
            {
                double r = wc.MapAmpForTest(pts[i].Max[0]);
                if (r > maxMapped) maxMapped = r;
            }
            Core.Log.Write(string.Format("[wave-test] 点数={0} log={1} 静态模式={2} 静态点数={3} 最大映射={4:0.000} 缩放={5} 宽度={6:0.0}",
                pointCount, logScale, wc.StaticForTest, wc.PointCountForTest, maxMapped, wc.PixelsPerPoint, wc.ActualWidth));

            var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(wc);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            var name = string.Format("wave-{0}{1}{2}.png",
                logScale ? "log-" : "",
                pointCount,
                zoom != 1.0 ? "-z" + zoom.ToString("0.#") : "");
            var path = Path.Combine(outDir, name);
            using (var fs = File.Create(path)) enc.Save(fs);
        }

        /// <summary>
        /// 用「实时模式」渲染三段不同音量，专门验证振幅染色与削波分层：
        /// 前段由极轻渐强到 −1 dBFS（渐变表应走完 蓝→青→绿→黄→琥珀→橙），
        /// 中段从 −1 dBFS 撞到满幅（应为橙红录警），
        /// 后段超过满幅（应为纯红）。
        /// 与 RenderWaveform 的区别：不走 SetStatic，所以走的是实时染色分支。
        /// </summary>
        private static void RenderLiveColor(string outDir)
        {
            const int W = 800, H = 140;
            var wc = new WaveformControl { Width = W, Height = H };
            wc.Configure(1);
            wc.SetScrollSpeed(48000, 720);

            // 按 16 bit 满幅走，和默认阈值一致，三档边界才可录期
            double clip = ClipLevel.FullScale(16);
            double warn = ClipLevel.WarnScale(16);
            wc.SetClipThreshold(clip);

            const int N = 700;
            var pts = new PeakPoint[N];
            for (int i = 0; i < N; i++)
            {
                double t = i / (double)(N - 1);
                double v;
                if (t < 0.60)
                {
                    // 渐变段：几乎无声 → 录警线
                    v = Math.Pow(t / 0.60, 2.0) * warn;
                }
                else if (t < 0.80)
                {
                    // near-clip 段：录警线 → 满幅
                    v = warn + (clip - warn) * ((t - 0.60) / 0.20);
                }
                else
                {
                    // 削波段：前半段轻微超出满幅（1.02~1.08 倍），后半段直接顶到 +6 dB（2 倍封顶），
                    // 用来验证顶部过载余量区能不能正确画出「超出多少」
                    double k = (t - 0.80) / 0.20;
                    v = k < 0.5
                        ? clip * (1.02 + 0.06 * Math.Sin(t * 60.0))
                        : clip * 2.0;
                }
                pts[i] = new PeakPoint { Min = new[] { (float)(-v) }, Max = new[] { (float)v } };
            }
            wc.Append(pts);
            wc.SnapToLatest();
            wc.Measure(new Size(W, H));
            wc.Arrange(new Rect(0, 0, W, H));
            wc.UpdateLayout();

            var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(wc);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            var path = Path.Combine(outDir, "wave-live-color.png");
            using (var fs = File.Create(path)) enc.Save(fs);
            Core.Log.Write("[wave-test] 染色测试图: " + path);
        }

        /// <summary>
        /// 把一串由低到高的电平渲染成电平条，验证「渐变按整条宽度铺、只用裁剪露出可见段」：
        /// 电平越低，能看到的颜色应越靠前（绿打头），而不是被压成一小段彩虹。
        /// </summary>
        private static void RenderLevelMeter(string outDir)
        {
            const int W = 400, H = 14, Gap = 6;
            var levels = new[] { 0.15f, 0.35f, 0.55f, 0.75f, 0.92f, 1.0f };
            int totalH = levels.Length * (H + Gap);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, W, totalH));
                double y = Gap / 2.0;
                foreach (var lv in levels)
                {
                    var lm = new LevelMeter { Width = W, Height = H };
                    lm.SetFullScale(ClipLevel.FullScale(16));
                    lm.SetLevel(lv);      // 首次调用 level >= 0，直接建立显示值，没有平滑过程
                    lm.Measure(new Size(W, H));
                    lm.Arrange(new Rect(0, 0, W, H));
                    lm.UpdateLayout();

                    var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(lm);
                    dc.DrawImage(rtb, new Rect(0, y, W, H));
                    y += H + Gap;
                }
            }

            var final = new RenderTargetBitmap(W, totalH, 96, 96, PixelFormats.Pbgra32);
            final.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(final));
            var path = Path.Combine(outDir, "meter-levels.png");
            using (var fs = File.Create(path)) enc.Save(fs);
            Core.Log.Write("[meter-test] 电平条分级图: " + path);
        }

        /// <summary>
        /// 纵向电平表：由低到高排开，最后两格进入 +6 dB 余量区，
        /// 验证「0 dB 落在余量区下边界」「刻度标得出来」「超过满幅还能往上走」。
        /// </summary>
        private static void RenderMeterVertical(string outDir)
        {
            const int W = 46, H = 220, Gap = 10;
            var levels = new[] { 0.25f, 0.55f, 0.85f, 1.0f, 1.35f };
            int totalW = levels.Length * (W + Gap);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, totalW, H));
                double x = 0;
                foreach (var lv in levels)
                {
                    var lm = new LevelMeter { Width = W, Height = H, IsVertical = true, ShowScale = true };
                    lm.SetFullScale(ClipLevel.FullScale(16));
                    lm.SetLevel(lv);
                    lm.Measure(new Size(W, H));
                    lm.Arrange(new Rect(0, 0, W, H));
                    lm.UpdateLayout();

                    var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(lm);
                    dc.DrawImage(rtb, new Rect(x, 0, W, H));
                    x += W + Gap;
                }
            }

            var final = new RenderTargetBitmap(totalW, H, 96, 96, PixelFormats.Pbgra32);
            final.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(final));
            var path = Path.Combine(outDir, "meter-vertical.png");
            using (var fs = File.Create(path)) enc.Save(fs);
            Core.Log.Write("[meter-test] 纵向电平条图: " + path);
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint dwFlags);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [System.Runtime.InteropServices.DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [System.Runtime.InteropServices.DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        public static void Run(string logPath)
        {
            var sb = new StringBuilder();
            try
            {
                sb.AppendLine("=== RecLite 自检 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                sb.AppendLine("BaseDir = " + AppPaths.BaseDir);

                var srcs = DeviceService.GetRecordingSources();
                sb.AppendLine("录音源数量 = " + srcs.Count);
                foreach (var s in srcs.Take(10))
                    sb.AppendLine("  [" + s.Kind + "] " + s.Name + " | " + s.FormatSummary);

                var pls = DeviceService.GetPlaybackDevices();
                sb.AppendLine("播放设备数量 = " + pls.Count);

                var target = srcs.FirstOrDefault(x => x.Kind == DeviceKind.Capture) ?? srcs.FirstOrDefault();
                if (target == null)
                {
                    sb.AppendLine("!! 没有可用录音源");
                }
                else
                {
                    sb.AppendLine("选中设备 = " + target.Name + " (" + target.Kind + ")");
                    var dev = DeviceService.FindById(target.Id);
                    if (dev == null) sb.AppendLine("!! 无法按 Id 取回设备");
                    else
                    {
                        using (var eng = new RecordingEngine())
                        {
                            eng.Error += m => sb.AppendLine("  [ERR] " + m);
                            eng.StartDevice(dev, target.Kind, 1);
                            sb.AppendLine(string.Format("设备已打开 = {0} Hz / {1} ch / {2} bit (float={3})",
                                eng.SampleRate, eng.Channels, eng.BitsPerSample,
                                eng.SampleRate > 0));

                            Thread.Sleep(1600);   // 积累预录
                            sb.AppendLine("预录缓存 = " + eng.PrerollSeconds.ToString("0.00") + " 秒");

                            var outDir = Path.Combine(AppPaths.BaseDir, "selftest-out");
                            Directory.CreateDirectory(outDir);
                            var wav = Path.Combine(outDir, "selftest.wav");
                            if (File.Exists(wav)) File.Delete(wav);

                            eng.StartRecording(wav, "wav", 16, 192, 0);
                            Thread.Sleep(2000);   // 录 2 秒
                            long frames = eng.StopRecording();
                            sb.AppendLine("录制帧数 = " + frames);
                            sb.AppendLine("录制秒数 = " + (frames / (double)Math.Max(1, eng.SampleRate)).ToString("0.00"));

                            Thread.Sleep(300);
                            if (File.Exists(wav))
                            {
                                var fi = new FileInfo(wav);
                                sb.AppendLine("WAV 大小 = " + fi.Length + " bytes");
                                using (var fs = File.OpenRead(wav))
                                {
                                    var head = new byte[12];
                                    fs.Read(head, 0, 12);
                                    sb.AppendLine("WAV 头 = " + BitConverter.ToString(head));
                                    sb.AppendLine("RIFF 校验 = " + (Encoding.ASCII.GetString(head, 0, 4) == "RIFF" && Encoding.ASCII.GetString(head, 8, 4) == "WAVE"));
                                }

                                // MP3 转码链路测试
                                var mp3 = Path.Combine(outDir, "selftest.mp3");
                                if (File.Exists(mp3)) File.Delete(mp3);
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                bool ok = Mp3Converter.Convert(wav, mp3, 192);
                                sw.Stop();
                                sb.AppendLine(string.Format("MP3 转码 = {0}, 耗时 = {1} ms, 大小 = {2} bytes",
                                    ok, sw.ElapsedMilliseconds, File.Exists(mp3) ? new FileInfo(mp3).Length : 0));
                            }
                            else sb.AppendLine("!! WAV 未生成");

                            // ---- 32bit 浮点录制测试（GUI 默认格式，重点验证）----
                            var wav32 = Path.Combine(outDir, "selftest-32f.wav");
                            if (File.Exists(wav32)) File.Delete(wav32);
                            eng.StartRecording(wav32, "wav", 32, 0, 0);
                            Thread.Sleep(1000);
                            long f32 = eng.StopRecording();
                            long s32 = File.Exists(wav32) ? new FileInfo(wav32).Length : 0;
                            sb.AppendLine(string.Format("32bit浮点录制 = {0} 帧, 文件 = {1} bytes", f32, s32));

                            // ---- 连续第二次录制（验证停止后再录不失效）----
                            var wav2 = Path.Combine(outDir, "selftest-2nd.wav");
                            if (File.Exists(wav2)) File.Delete(wav2);
                            eng.StartRecording(wav2, "wav", 16, 192, 0);
                            Thread.Sleep(800);
                            long f2 = eng.StopRecording();
                            sb.AppendLine(string.Format("第二次录制 = {0} 帧, 文件 = {1} bytes",
                                f2, File.Exists(wav2) ? new FileInfo(wav2).Length : 0));

                            // ---- 完整保存流程：临时文件 + AudioSaver → 最终文件（与 GUI 同一份代码）----
                            var t1 = Path.Combine(outDir, "flow1.tmp.wav");
                            if (File.Exists(t1)) File.Delete(t1);
                            eng.StartRecording(t1, "wav", 32, 0, 0);
                            Thread.Sleep(1200);
                            eng.StopRecording();
                            var fin1 = Path.Combine(outDir, "完整保存_测试.wav");
                            var sr1 = AudioSaver.Save(t1, fin1, false, 192, 0, eng.SampleRate);
                            sb.AppendLine(string.Format("保存流程[WAV 32bit/快速路径] => {0}, 大小={1}",
                                sr1.Ok ? "成功" : "失败(" + sr1.Error + ")",
                                File.Exists(sr1.FinalPath) ? new FileInfo(sr1.FinalPath).Length : 0));

                            var t2 = Path.Combine(outDir, "flow2.tmp.wav");
                            if (File.Exists(t2)) File.Delete(t2);
                            eng.StartRecording(t2, "wav", 16, 192, 0);
                            Thread.Sleep(1000);
                            eng.StopRecording();
                            var fin2 = Path.Combine(outDir, "完整保存_测试.mp3");
                            var sr2 = AudioSaver.Save(t2, fin2, true, 192, 0, eng.SampleRate);
                            sb.AppendLine(string.Format("保存流程[MP3 192k] => {0}, 大小={1}",
                                sr2.Ok ? "成功" : "失败(" + sr2.Error + ")",
                                File.Exists(sr2.FinalPath) ? new FileInfo(sr2.FinalPath).Length : 0));

                            // ---- 中文目录 + 中文文件名（模拟真实使用）----
                            var cnDir = Path.Combine(outDir, "中文目录测试");
                            Directory.CreateDirectory(cnDir);
                            var t3 = Path.Combine(cnDir, "流程3.tmp.wav");
                            if (File.Exists(t3)) File.Delete(t3);
                            eng.StartRecording(t3, "wav", 32, 0, 0);
                            Thread.Sleep(600);
                            eng.StopRecording();
                            var fin3 = Path.Combine(cnDir, "录音_20260101_120000.wav");
                            var sr3 = AudioSaver.Save(t3, fin3, false, 192, 0, eng.SampleRate);
                            sb.AppendLine(string.Format("保存流程[中文路径] => {0}, 大小={1}",
                                sr3.Ok ? "成功" : "失败(" + sr3.Error + ")",
                                File.Exists(sr3.FinalPath) ? new FileInfo(sr3.FinalPath).Length : 0));

                            // ---- 预录开关测试（关键回归点）----
                            // (a) 关闭预录：先空等 2 秒，再录 2 秒，结果应 ≈ 2 秒（不含空等那段）
                            var devA = DeviceService.FindById(target.Id);
                            eng.StopDevice();
                            eng.StartDevice(devA, target.Kind, 0);
                            Thread.Sleep(2000);
                            var tA = Path.Combine(outDir, "preroll-off.wav");
                            if (File.Exists(tA)) File.Delete(tA);
                            eng.StartRecording(tA, "wav", 16, 192, 0);
                            Thread.Sleep(2000);
                            long fA = eng.StopRecording();
                            sb.AppendLine(string.Format("预录=关闭(空等2s后录2s) => {0:0.00} 秒 [期望≈2.00]",
                                fA / (double)eng.SampleRate));

                            // (b) 预录=10 秒：空等 5 秒后录 2 秒，结果应 ≈ 7 秒
                            var devB = DeviceService.FindById(target.Id);
                            eng.StopDevice();
                            eng.StartDevice(devB, target.Kind, 10);
                            Thread.Sleep(5000);
                            var tB = Path.Combine(outDir, "preroll-10s.wav");
                            if (File.Exists(tB)) File.Delete(tB);
                            eng.StartRecording(tB, "wav", 16, 192, 0);
                            Thread.Sleep(2000);
                            long fB = eng.StopRecording();
                            sb.AppendLine(string.Format("预录=10秒(空等5s后录2s) => {0:0.00} 秒 [期望≈7.00]",
                                fB / (double)eng.SampleRate));

                            // (c) 运行中改设置：预录 10s → 改成 0 → 录 2 秒应 ≈ 2 秒
                            var devC = DeviceService.FindById(target.Id);
                            eng.StopDevice();
                            eng.StartDevice(devC, target.Kind, 10);
                            Thread.Sleep(4000);
                            eng.SetPrerollSeconds(0);      // 模拟用户在下拉框选「关闭」
                            Thread.Sleep(1000);
                            var tC = Path.Combine(outDir, "preroll-runtime-change.wav");
                            if (File.Exists(tC)) File.Delete(tC);
                            eng.StartRecording(tC, "wav", 16, 192, 0);
                            Thread.Sleep(2000);
                            long fC = eng.StopRecording();
                            sb.AppendLine(string.Format("运行中改为关闭 => {0:0.00} 秒 [期望≈2.00]",
                                fC / (double)eng.SampleRate));

                            // (d) 暂停测试：录 1s → 暂停 1s → 再录 1s，结果应 ≈ 2s
                            eng.StopDevice();
                            eng.StartDevice(DeviceService.FindById(target.Id), target.Kind, 0);
                            var tD = Path.Combine(outDir, "pause-test.wav");
                            if (File.Exists(tD)) File.Delete(tD);
                            eng.StartRecording(tD, "wav", 16, 192, 0, false);
                            Thread.Sleep(1000);
                            eng.PauseRecording();
                            Thread.Sleep(1000);
                            eng.ResumeRecording();
                            Thread.Sleep(1000);
                            long fD = eng.StopRecording();
                            sb.AppendLine(string.Format("暂停测试(1s+暂停1s+1s) => {0:0.00} 秒 [期望≈2.00]",
                                fD / (double)eng.SampleRate));

                            // (e) 开了预录但选择不包含：预录10s、空等5s、录2s，应 ≈ 2s
                            eng.StopDevice();
                            eng.StartDevice(DeviceService.FindById(target.Id), target.Kind, 10);
                            Thread.Sleep(5000);
                            var tE = Path.Combine(outDir, "no-preroll.wav");
                            if (File.Exists(tE)) File.Delete(tE);
                            eng.StartRecording(tE, "wav", 16, 192, 0, false);
                            Thread.Sleep(2000);
                            long fE = eng.StopRecording();
                            sb.AppendLine(string.Format("开预录但选择不含 => {0:0.00} 秒 [期望≈2.00]",
                                fE / (double)eng.SampleRate));

                            // (f) 增益测试：2.0x
                            eng.InputGain = 2.0f;
                            eng.StopDevice();
                            eng.StartDevice(DeviceService.FindById(target.Id), target.Kind, 0);
                            var tF = Path.Combine(outDir, "gain-2x.wav");
                            if (File.Exists(tF)) File.Delete(tF);
                            eng.StartRecording(tF, "wav", 16, 192, 0, false);
                            Thread.Sleep(1000);
                            long fF = eng.StopRecording();
                            eng.InputGain = 1.0f;
                            sb.AppendLine(string.Format("增益2.0x录制 => {0:0.00} 秒, 文件 {1} bytes",
                                fF / (double)eng.SampleRate,
                                File.Exists(tF) ? new FileInfo(tF).Length : 0));
                        }

                        // ---- 波形渲染验证：不同点数下波形应保持固定密度（不拉伸铺满）----
                        try
                        {
                            var waveOut = Path.Combine(AppPaths.BaseDir, "selftest-out");
                            Directory.CreateDirectory(waveOut);
                            var cases = new[] { 100, 400, 800, 2000 };
                            foreach (var n in cases) RenderWaveform(waveOut, n, false);
                            RenderWaveform(waveOut, 800, true);
                            RenderWaveform(waveOut, 800, false, 2.0);
                            RenderWaveform(waveOut, 800, false, 4.0);
                            RenderLiveColor(waveOut);
                            RenderLevelMeter(waveOut);
                            RenderMeterVertical(waveOut);
                            sb.AppendLine("波形渲染测试图: wave-{100,400,800,2000}.png + wave-log-800.png + 缩放 wave-800-z2/z4.png");

                            // ---- A+B 增量验证：状态不变时不应重复绘制像素 ----
                            var wc2 = new WaveformControl { Width = 400, Height = 100 };
                            wc2.Configure(1);
                            var pts2 = new PeakPoint[100];
                            for (int i = 0; i < pts2.Length; i++)
                                pts2[i] = new PeakPoint { Min = new[] { -0.5f }, Max = new[] { 0.5f } };
                            wc2.SetStatic(pts2, 1);
                            wc2.Measure(new Size(400, 100));
                            wc2.Arrange(new Rect(0, 0, 400, 100));

                            var rtb2 = new RenderTargetBitmap(400, 100, 96, 96, PixelFormats.Pbgra32);
                            for (int i = 0; i < 5; i++) rtb2.Render(wc2);
                            long afterIdle = wc2.RenderCountForTest;

                            var more = new List<PeakPoint>(pts2);
                            for (int i = 0; i < 10; i++)
                                more.Add(new PeakPoint { Min = new[] { -0.6f }, Max = new[] { 0.6f } });
                            wc2.SetStatic(more.ToArray(), 1);
                            for (int i = 0; i < 5; i++) rtb2.Render(wc2);
                            long afterChange = wc2.RenderCountForTest;

                            sb.AppendLine(string.Format("A+B增量验证: 连续5帧无变化后重绘={0}次, 数据变化后再5帧={1}次（理想 1 和 2）",
                                afterIdle, afterChange));
                        }
                        catch (Exception ex) { sb.AppendLine("波形渲染测试失败: " + ex.Message); }
                    }
                }

                sb.AppendLine("MP3 编码器可用 = " + Mp3Converter.IsAvailable());

                // ---- Shell API 测试（验证 64 位构建是否解决了 0x80070005）----
                try
                {
                    sb.AppendLine("进程位数 = " + (IntPtr.Size * 8) + " 位");

                    IntPtr pidl;
                    uint attrs;
                    int hr1 = SHParseDisplayName(AppPaths.BaseDir, IntPtr.Zero, out pidl, 0, out attrs);
                    int hr2 = -1;
                    if (hr1 == 0 && pidl != IntPtr.Zero)
                    {
                        hr2 = SHOpenFolderAndSelectItems(pidl, 0, null, 0);
                        ILFree(pidl);
                    }
                    sb.AppendLine(string.Format("Shell API: SHParseDisplayName=0x{0:X8}, SHOpenFolderAndSelectItems=0x{1:X8}",
                        hr1, hr2));

                    // 显式初始化 STA 后再试一次（排查 COM 上下文问题）
                    IntPtr pidl2;
                    uint attrs2;
                    if (SHParseDisplayName(AppPaths.BaseDir, IntPtr.Zero, out pidl2, 0, out attrs2) == 0 && pidl2 != IntPtr.Zero)
                    {
                        int hrInit = CoInitializeEx(IntPtr.Zero, 0x2);   // COINIT_APARTMENTTHREADED
                        int hr3 = SHOpenFolderAndSelectItems(pidl2, 0, null, 0);
                        if (hrInit >= 0) CoUninitialize();
                        ILFree(pidl2);
                        sb.AppendLine(string.Format("显式CoInit后: CoInitializeEx=0x{0:X8}（1=S_FALSE表示已是STA）, 调用=0x{1:X8}",
                            hrInit, hr3));
                    }
                }
                catch (Exception ex) { sb.AppendLine("Shell API 测试异常: " + ex.Message); }
            }
            catch (Exception ex)
            {
                sb.AppendLine("EXCEPTION: " + ex);
            }
            sb.AppendLine("=== 结束 ===");

            try
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
