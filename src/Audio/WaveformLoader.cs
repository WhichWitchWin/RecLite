using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace RecLite.Audio
{
    /// <summary>
    /// 从音频文件生成波形峰值包络（用于试听时显示文件波形）。
    /// </summary>
    public static class WaveformLoader
    {
        private const int SamplesPerPoint = 512;

        /// <summary>读取文件并生成波形点；点数超过 maxPoints 时自动合并降采样</summary>
        public static PeakPoint[] Load(string path, int maxPoints, out int channels)
        {
            channels = 2;
            var points = new List<PeakPoint>();
            try
            {
                if (!File.Exists(path)) return points.ToArray();

                using (var reader = new AudioFileReader(path))
                {
                    int ch = Math.Max(1, reader.WaveFormat.Channels);
                    channels = ch;

                    // 诊断：把解码后的实际格式和峰值打出来，
                    // 用来区分「波形画得小」是文件本身小，还是解码器归一化不对
                    Core.Log.Write(string.Format("[波形] {0}: {1} Hz · {2} ch · {3} · {4} bit",
                        Path.GetFileName(path), reader.WaveFormat.SampleRate, ch,
                        reader.WaveFormat.Encoding, reader.WaveFormat.BitsPerSample));

                    float rawPeak = 0f;

                    var buf = new float[8192 * ch];
                    var curMin = new float[ch];
                    var curMax = new float[ch];
                    for (int c = 0; c < ch; c++) { curMin[c] = float.MaxValue; curMax[c] = float.MinValue; }
                    int accum = 0;
                    int read;

                    while ((read = reader.Read(buf, 0, buf.Length)) > 0)
                    {
                        int frames = read / ch;
                        for (int f = 0; f < frames; f++)
                        {
                            int b = f * ch;
                            for (int c = 0; c < ch; c++)
                            {
                                float v = buf[b + c];
                                float av = v < 0 ? -v : v;
                                if (av > rawPeak) rawPeak = av;
                                if (v < curMin[c]) curMin[c] = v;
                                if (v > curMax[c]) curMax[c] = v;
                            }
                            if (++accum >= SamplesPerPoint)
                            {
                                points.Add(new PeakPoint
                                {
                                    Min = (float[])curMin.Clone(),
                                    Max = (float[])curMax.Clone()
                                });
                                for (int c = 0; c < ch; c++) { curMin[c] = float.MaxValue; curMax[c] = float.MinValue; }
                                accum = 0;
                            }
                        }
                    }

                    Core.Log.Write(string.Format("[波形] {0}: {1} 点, 解码峰值 = {2:0.00000} ({3:0.0} dBFS)",
                        Path.GetFileName(path), points.Count, rawPeak,
                        rawPeak > 0 ? 20.0 * Math.Log10(rawPeak) : -999.0));
                }

                // 点数过多时合并，保证绘制开销可控
                if (maxPoints > 0 && points.Count > maxPoints)
                    points = Merge(points, maxPoints, channels);
            }
            catch (Exception) { }
            return points.ToArray();
        }

        private static List<PeakPoint> Merge(List<PeakPoint> src, int target, int ch)
        {
            int factor = (int)Math.Ceiling(src.Count / (double)target);
            if (factor < 2) return src;

            var merged = new List<PeakPoint>(target + 1);
            for (int i = 0; i < src.Count; i += factor)
            {
                var mn = new float[ch];
                var mx = new float[ch];
                for (int c = 0; c < ch; c++) { mn[c] = float.MaxValue; mx[c] = float.MinValue; }

                int end = Math.Min(i + factor, src.Count);
                for (int j = i; j < end; j++)
                {
                    var p = src[j];
                    for (int c = 0; c < ch; c++)
                    {
                        float a = (p.Min != null && c < p.Min.Length) ? p.Min[c] : 0;
                        float b = (p.Max != null && c < p.Max.Length) ? p.Max[c] : 0;
                        if (a < mn[c]) mn[c] = a;
                        if (b > mx[c]) mx[c] = b;
                    }
                }
                merged.Add(new PeakPoint { Min = mn, Max = mx });
            }
            return merged;
        }
    }
}
