using System;
using System.IO;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace RecLite.Audio
{
    /// <summary>
    /// MP3 转码器。
    ///
    /// 采用策略：录制时先写 WAV 临时文件，停止后转码为 MP3，成功后删除临时 WAV。
    /// 好处是完全不依赖第三方编码库（用 Windows 自带 Media Foundation），保持轻量。
    /// 注意：MP3 只支持 32 / 44.1 / 48 kHz，其他采样率会自动重采样。
    /// </summary>
    public static class Mp3Converter
    {
        /// <summary>检测本机是否有 MP3 编码器（精简版 Windows 可能没有）</summary>
        public static bool IsAvailable()
        {
            try
            {
                MediaFoundationApi.Startup();
                return MediaFoundationEncoder.SelectMediaType(
                    AudioSubtypes.MFAudioFormat_MP3,
                    new WaveFormat(48000, 16, 2),
                    192000) != null;
            }
            catch (Exception) { return false; }
        }

        /// <summary>把 WAV 转成 MP3；成功返回 true</summary>
        /// <param name="targetSampleRate">目标采样率；0 = 自动（保持源采样率，不支持则转 48k）</param>
        public static bool Convert(string wavPath, string mp3Path, int bitrateKbps, int targetSampleRate = 0)
        {
            if (!File.Exists(wavPath)) throw new FileNotFoundException("源文件不存在", wavPath);
            if (bitrateKbps < 32) bitrateKbps = 32;
            if (bitrateKbps > 320) bitrateKbps = 320;

            var dir = Path.GetDirectoryName(mp3Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var reader = new WaveFileReader(wavPath))
            {
                int srcRate = reader.WaveFormat.SampleRate;
                int ch = reader.WaveFormat.Channels;

                bool wantValid = targetSampleRate == 32000 || targetSampleRate == 44100 || targetSampleRate == 48000;
                int effective = wantValid ? targetSampleRate : 0;

                IWaveProvider source = reader;
                MediaFoundationResampler resampler = null;
                try
                {
                    if (effective > 0 && effective != srcRate)
                    {
                        // 用户指定了合法 MP3 采样率，且与源不同 → 重采样
                        resampler = new MediaFoundationResampler(reader, new WaveFormat(effective, ch));
                        source = resampler;
                    }
                    else if (effective == 0 && srcRate != 32000 && srcRate != 44100 && srcRate != 48000)
                    {
                        // MP3 不支持该采样率 → 转 48000
                        resampler = new MediaFoundationResampler(reader, new WaveFormat(48000, ch));
                        source = resampler;
                    }

                    MediaFoundationEncoder.EncodeToMp3(source, mp3Path, bitrateKbps);
                }
                finally
                {
                    // 原来编码抛异常时直接跳过 Dispose，重采样器（持有 COM/MediaFoundation 资源）
                    // 只能等 GC 收尾；这里放进 finally 保证一定释放。
                    if (resampler != null) { try { resampler.Dispose(); } catch (Exception) { } }
                }
            }

            if (!File.Exists(mp3Path)) return false;
            return new FileInfo(mp3Path).Length > 0;
        }
    }
}
