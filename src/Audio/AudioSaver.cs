using System;
using System.IO;
using NAudio.Wave;

namespace RecLite.Audio
{
    public class SaveResult
    {
        public bool Ok;
        public string FinalPath;
        public string Error;
    }

    /// <summary>
    /// 录音落盘器：把「录制时写出的临时 WAV」变成「最终文件」。
    ///
    /// 抽成独立类的目的：让 GUI 和自检程序走完全相同的代码路径，
    /// 自检能真正验证保存环节，而不是各写一套。
    /// </summary>
    public static class AudioSaver
    {
        /// <param name="tempWav">录制时写出的临时 WAV</param>
        /// <param name="finalPath">最终目标路径（.wav 或 .mp3）</param>
        /// <param name="mp3">是否输出 MP3</param>
        /// <param name="bitrate">MP3 码率 kbps</param>
        /// <param name="targetRate">目标采样率；0 或不等于源 → 按需重采样</param>
        /// <param name="srcRate">源采样率</param>
        public static SaveResult Save(string tempWav, string finalPath, bool mp3, int bitrate, int targetRate, int srcRate)
        {
            var r = new SaveResult();
            try
            {
                if (string.IsNullOrEmpty(tempWav) || !File.Exists(tempWav))
                {
                    r.Error = "临时文件不存在：" + (tempWav ?? "null");
                    return r;
                }

                var dir = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                bool needResample = targetRate > 0 && targetRate != srcRate;

                // 简单情况：不需要转码也不需要重采样 → 直接改名
                if (!mp3 && !needResample)
                {
                    if (File.Exists(finalPath)) File.Delete(finalPath);
                    File.Move(tempWav, finalPath);
                    r.Ok = true;
                    r.FinalPath = finalPath;
                    return r;
                }

                // WAV + 重采样
                if (!mp3)
                {
                    using (var reader = new WaveFileReader(tempWav))
                    using (var resampler = new MediaFoundationResampler(reader,
                        new WaveFormat(targetRate, reader.WaveFormat.Channels)))
                    {
                        WaveFileWriter.CreateWaveFile(finalPath, resampler);
                    }
                }
                else
                {
                    Mp3Converter.Convert(tempWav, finalPath, bitrate, needResample ? targetRate : 0);
                }

                try { File.Delete(tempWav); } catch { }

                if (!File.Exists(finalPath) || new FileInfo(finalPath).Length == 0)
                {
                    r.Error = "输出文件为空";
                    return r;
                }

                r.Ok = true;
                r.FinalPath = finalPath;
                return r;
            }
            catch (Exception ex)
            {
                r.Error = ex.GetType().Name + ": " + ex.Message;
                return r;
            }
        }

        /// <summary>
        /// 把 WAV 开头的若干帧裁掉，重写到 dst。
        ///
        /// 用途：录制时开头总是先写进预录段（这样停止时才有得选），
        /// 用户点「停止（不含预录）」时就用这个把开头那段砍掉。
        /// 保留原文件的位深与采样率（32 位浮点原样搬，不做任何换算）。
        /// </summary>
        /// <param name="src">源 WAV</param>
        /// <param name="dst">输出 WAV（必须与 src 不同）</param>
        /// <param name="framesToSkip">要从开头丢掉的帧数</param>
        public static bool TrimLeadingFrames(string src, string dst, long framesToSkip, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(src))
                {
                    error = "源文件不存在";
                    return false;
                }

                // 调用点保证传入 > 0；保留这条防御分支，让函数本身也能安全处理「不用裁」的情况
                if (framesToSkip <= 0)
                {
                    File.Copy(src, dst, true);
                    return true;
                }

                using (var reader = new WaveFileReader(src))
                {
                    var fmt = reader.WaveFormat;
                    long skipBytes = framesToSkip * fmt.BlockAlign;

                    // 预录段把整段录音都盖住了（按了录音马上就停）—— 没有可留的内容
                    if (skipBytes >= reader.Length)
                    {
                        error = "预录段覆盖了整段录音";
                        return false;
                    }

                    reader.Position = skipBytes;   // WaveFileReader.Position 相对数据段起点
                    using (var writer = new WaveFileWriter(dst, fmt))
                    {
                        var buf = new byte[64 * 1024];
                        int n;
                        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                            writer.Write(buf, 0, n);
                    }
                }

                // 用「数据块长度 > 0」判定，而不是「文件 ≤ 44 字节」：
                // 32 位浮点 WAV 可能带 fact 块、头长超过 44，零帧文件能骗过按文件大小的判断。
                if (!File.Exists(dst))
                {
                    error = "裁剪后没有输出文件";
                    return false;
                }
                using (var check = new WaveFileReader(dst))
                {
                    if (check.Length <= 0)
                    {
                        error = "裁剪后文件为空";
                        // 失败就别把半成品留在用户目录里：本函数自己产出的垃圾自己收。
                        TryDeleteQuiet(dst);
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                TryDeleteQuiet(dst);   // 中途抛异常同样可能留下半个文件
                return false;
            }
        }

        /// <summary>删掉失败留下的半成品；删不掉也不该让调用方失败</summary>
        private static void TryDeleteQuiet(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
