using System;
using NAudio.Wave;

namespace RecLite
{
    /// <summary>
    /// 播放链路上的「电平中继 + 定位执行器」。
    ///
    /// 两个职责：
    ///  1. 记录流过的样本峰值，供输出电平表读取（不影响音频）；
    ///  2. 承接 seek 请求，并在 Read 里真正执行。
    ///
    /// 为什么 seek 要放在这里：
    /// WasapiOut.Stop() 会等播放线程退出，如果在 UI 线程调它，而播放线程恰好卡在 Read 上，
    /// 两边就互相等死（实测日志停在「seek: 调用 Stop」之后再也没有下文）。
    /// Read 是唯一读取这个流的线程，在这里改位置不存在任何竞态，也不会阻塞 UI。
    /// </summary>
    public class MeteringProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly WaveStream _seekable;

        private long _pendingSeekFrame = -1;

        public MeteringProvider(ISampleProvider source, WaveStream seekable = null)
        {
            _source = source;
            _seekable = seekable;
        }

        public WaveFormat WaveFormat { get { return _source.WaveFormat; } }

        /// <summary>最近一次 Read 里出现的最大绝对值。平滑交给 LevelMeter 做，这里不重复处理。</summary>
        public float Peak { get; private set; }

        /// <summary>
        /// 请求跳到指定秒数。只登记，不执行 —— 真正的跳转发生在下一次 Read 里（音频线程）。
        /// </summary>
        public void RequestSeek(double seconds)
        {
            if (_seekable == null) return;
            if (seconds < 0) seconds = 0;
            _pendingSeekFrame = (long)(seconds * _seekable.WaveFormat.SampleRate);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // 在音频线程里执行挂起的 seek（探针记录执行前后）
            long pending = _pendingSeekFrame;
            if (pending >= 0 && _seekable != null)
            {
                _pendingSeekFrame = -1;
                try
                {
                    var wf = _seekable.WaveFormat;
                    long maxFrame = _seekable.Length / Math.Max(1, wf.BlockAlign);
                    if (pending > maxFrame) pending = maxFrame;

                    double secs = pending / (double)Math.Max(1, wf.SampleRate);
                    _seekable.CurrentTime = TimeSpan.FromSeconds(secs);
                }
                catch (Exception ex)
                {
                    Core.Log.Error("MeteringProvider.Seek", ex);
                }
            }

            int n = _source.Read(buffer, offset, count);

            float m = 0f;
            for (int i = 0; i < n; i++)
            {
                float a = buffer[offset + i];
                if (a < 0f) a = -a;
                if (a > m) m = a;
            }
            Peak = m;
            return n;
        }
    }
}
