using System;

namespace RecLite
{
    /// <summary>
    /// 削波判定基准。
    ///
    /// 为什么按「位深」算而不是写死一个数：
    /// 数字音频的满幅值由格式决定 —— 16 bit 最大是 32767/32768，
    /// 24 bit 是 8388607/8388608，32 bit 浮点则是 1.0（浮点本身能表示更大，
    /// 但超过 1.0 就是超出 0 dBFS，送到 DAC 一样会被削）。所以同一个振幅
    /// 值在不同位深下「是否已经撞满」的答案不同，必须按当前选中的位深判。
    ///
    /// 采样与运算全程保持 float，不降到 int16 —— 量化会丢信息，而且 16 bit
    /// 的一步是 3.05e-5，恰好落在削波线附近，量化后真实峰值可能被判成
    /// 满幅、也可能漏判。
    /// </summary>
    public static class ClipLevel
    {
        /// <summary>near-clip 预警线：满幅的 −1 dBFS。</summary>
        /// <remarks>
        /// −1 dB 是 DAW 里最常用的「该收手了」提醒，给后续处理留 1 dB 余量。
        /// 10^(-1/20) = 0.8912509381337456
        /// </remarks>
        public const double WarnRatio = 0.8912509381337456;

        /// <summary>
        /// 位深 → 该格式的满幅振幅值。
        /// 0（跟随设备）按最严的 16 bit 处理，宁可早报也不漏报。
        /// </summary>
        public static double FullScale(int bitsPerSample)
        {
            switch (bitsPerSample)
            {
                case 24: return 8388607.0 / 8388608.0;   // ≈ 0.99999988
                case 32: return 1.0;                      // float 满幅（0 dBFS）
                case 16:
                default: return 32767.0 / 32768.0;        // ≈ 0.99996948
            }
        }

        /// <summary>位深 → 该格式的 near-clip 预警值。</summary>
        public static double WarnScale(int bitsPerSample)
        {
            return FullScale(bitsPerSample) * WarnRatio;
        }
    }
}
