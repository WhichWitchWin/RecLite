using System;
using System.Collections.Generic;

namespace RecLite.Audio
{
    /// <summary>
    /// 分析器接口 —— 可扩展点。
    /// 任何「旁挂」分析器（波形 / RMS / 短时响度 LUFS-S / 整体响度 LUFS-I / 声像）都实现它，
    /// 挂在采集回调上顺便看一眼数据，不影响录音主流程。
    /// </summary>
    public interface IAudioAnalyzer
    {
        /// <summary>开始（或重新开始）分析时调用</summary>
        void Reset(int sampleRate, int channels);

        /// <summary>采集回调里调用：interleaved 为 32bit 浮点交错采样，frames 为帧数</summary>
        void Process(float[] interleaved, int frames);
    }

    /// <summary>波形峰值点（每声道一组 min/max）</summary>
    public class PeakPoint
    {
        public float[] Min;
        public float[] Max;
    }

    /// <summary>
    /// 波形分析器：把高密度采样压成"峰值包络"。
    /// 48kHz 每秒约 94 个点，界面按帧取走绘制 —— 既流畅又不吃 CPU。
    /// </summary>
    public class WaveformAnalyzer : IAudioAnalyzer
    {
        /// <summary>
        /// 每多少个采样帧合并成一个波形点。
        ///
        /// 取 720：48kHz 下是 66.7 点/秒（原先 800，正好 60 点/秒）。
        ///
        /// 为什么改：实测本机屏幕刷新约 133Hz，而 133 ÷ 60 = 2.22 不是整数 ——
        /// 重绘只能「等 2 帧、等 3 帧」交替，虽然每次位移都是 1 像素，
        /// 但一快一慢的节奏看着就是轻微顿挫。
        /// 改成 66.7 点/秒后，133 ÷ 66.7 ≈ 2，几乎正好「两帧走一个像素」，节奏就均匀了。
        ///
        /// 代价：横向精度降约 11%，肉眼不可见。
        /// 注意：这不是万能值 —— 换到 60Hz 屏幕（60 ÷ 66.7 ≈ 0.9）反而会变不匀。
        /// 整数像素滚动无法同时适配所有刷新率，这是算术限制，不是代码问题。
        /// </summary>
        public const int SamplesPerPeak = 720;

        /// <summary>
        /// 波形数据点上限（界面保留多少个峰值点）。
        /// 48kHz 下约 66.67 点/秒，40000 点 ≈ 10 分钟。
        /// 原来是 8000（只够 2 分钟），录长一点往回滚就只剩最后两分钟了。
        /// </summary>
        public const int MaxPoints = 40000;

        /// <summary>
        /// 给定采样率下，MaxPoints 个点能覆盖多少秒。
        ///
        /// 【纯函数】只吃参数、不读任何全局状态。采样率的唯一权威来源是
        /// RecordingEngine.SampleRate，由调用方传进来 —— 这里不再存第二份副本，
        /// 避免「两个地方各记一份、某条路径只更新了一个」这种最难查的错位。
        ///
        /// 整数除法天然向下取整，这个方向是故意的：
        /// 少算一点安全，多算一点波形就会缺一截、绿条和波形对不上。
        ///
        /// 例：40000 × 720 ÷ 48000 = 600 秒（48kHz）；÷ 96000 = 300 秒（96kHz）。
        /// </summary>
        public static int SecondsCoveredAt(int sampleRate)
        {
            int sr = sampleRate >= 8000 ? sampleRate : 48000;
            return MaxPoints * SamplesPerPeak / sr;
        }

        private readonly object _lock = new object();
        private readonly List<PeakPoint> _pending = new List<PeakPoint>();
        private int _channels = 2;
        private float[] _curMin = new float[2];
        private float[] _curMax = new float[2];
        // 上一个「完整窗口」的峰值。缓存清空重来时 _curMin/_curMax 会回到哨兵值，
        // 如果电平表此时直接读，就会被打回 0 —— 那正是电平表"闪"的根源。
        private float[] _lastMin = new float[2];
        private float[] _lastMax = new float[2];
        private int _accum;

        /// <summary>每帧最大保留的待取点，防止界面不动时无限增长</summary>
        private const int MaxPending = 20000;

        public void Reset(int sampleRate, int channels)
        {
            lock (_lock)
            {
                // sampleRate 这里用不上：本类不保存采样率（避免双数据源）。
                // 「点能覆盖多少秒」由调用方用 RecordingEngine.SampleRate 传给 SecondsCoveredAt。
                _channels = Math.Max(1, channels);
                _curMin = new float[_channels];
                _curMax = new float[_channels];
                _lastMin = new float[_channels];
                _lastMax = new float[_channels];
                _accum = 0;
                _pending.Clear();
                for (int c = 0; c < _channels; c++)
                {
                    _curMin[c] = float.MaxValue; _curMax[c] = float.MinValue;
                    _lastMin[c] = 0f; _lastMax[c] = 0f;
                }
            }
        }

        public void Process(float[] interleaved, int frames)
        {
            if (interleaved == null || frames <= 0) return;
            lock (_lock)
            {
                int ch = _channels;
                for (int f = 0; f < frames; f++)
                {
                    int baseIdx = f * ch;
                    for (int c = 0; c < ch; c++)
                    {
                        float v = interleaved[baseIdx + c];
                        if (v < _curMin[c]) _curMin[c] = v;
                        if (v > _curMax[c]) _curMax[c] = v;
                    }
                    if (++_accum >= SamplesPerPeak)
                    {
                        _pending.Add(new PeakPoint { Min = (float[])_curMin.Clone(), Max = (float[])_curMax.Clone() });
                        if (_pending.Count > MaxPending) _pending.RemoveRange(0, _pending.Count - MaxPending);
                        for (int c = 0; c < ch; c++)
                        {
                            // 先把本窗口峰值存起来，再清空 —— 电平表要靠它撑过清空瞬间
                            _lastMin[c] = _curMin[c];
                            _lastMax[c] = _curMax[c];
                            _curMin[c] = float.MaxValue;
                            _curMax[c] = float.MinValue;
                        }
                        _accum = 0;
                    }
                }
            }
        }

        /// <summary>取走自上次调用以来新产生的波形点</summary>
        public PeakPoint[] Drain()
        {
            lock (_lock)
            {
                if (_pending.Count == 0) return Array.Empty<PeakPoint>();
                var arr = _pending.ToArray();
                _pending.Clear();
                return arr;
            }
        }

        /// <summary>各声道当前瞬时峰值（用于电平表）</summary>
        public float[] CurrentPeak(int channels)
        {
            lock (_lock)
            {
                var r = new float[channels];
                for (int c = 0; c < channels && c < _channels; c++)
                {
                    // 缓存刚被清空时 _curMin/_curMax 还是哨兵值，此时退回上一个完整窗口的峰值；
                    // 并且始终与上一窗口取并集，避免电平值在窗口边界来回跳 —— 那会表现为闪烁。
                    float mn = (_curMin[c] == float.MaxValue) ? _lastMin[c] : Math.Min(_curMin[c], _lastMin[c]);
                    float mx = (_curMax[c] == float.MinValue) ? _lastMax[c] : Math.Max(_curMax[c], _lastMax[c]);
                    r[c] = Math.Max(Math.Abs(mn), Math.Abs(mx));
                }
                return r;
            }
        }
    }
}
