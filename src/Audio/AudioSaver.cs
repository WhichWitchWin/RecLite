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
    }
}
