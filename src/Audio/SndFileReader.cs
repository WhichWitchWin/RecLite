using System;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace RecLite.Audio
{
    /// <summary>
    /// 用 libsndfile 解码音频文件（WAV / AIFF / FLAC / OGG / MP3 / CAF / W64 / MAT 等）。
    ///
    /// 为什么不用 Windows 的 MediaFoundationReader：
    /// MF 的 FLAC 解码器在随机定位后会「返回成功但不再吐数据」——实测日志显示
    /// SetCurrentPosition 后 Read 再也不被调用，表现为没声音、时间静止，
    /// 随后 Stop() 死等那个卡住的音频线程，整个程序卡死。
    /// 而 libFLAC / libsndfile 是文件流定位，和 MP3 一样干净（QQ音乐、网易云、foobar、Reaper 都用这条）。
    ///
    /// 对外表现得像个普通 WaveStream，所以播放链路（MeteringProvider / WasapiOut）完全不用改。
    /// </summary>
    public class SndFileReader : WaveStream
    {
        private const int SFM_READ = 0x10;
        private const int SEEK_SET = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct SF_INFO
        {
            public long frames;
            public int samplerate;
            public int channels;
            public int format;
            public int sections;
            public int seekable;
        }

        [DllImport("sndfile.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr sf_open(string path, int mode, ref SF_INFO info);

        [DllImport("sndfile.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sf_readf_float(IntPtr sndfile, [In, Out] float[] ptr, long frames);

        [DllImport("sndfile.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sf_seek(IntPtr sndfile, long frames, int whence);

        [DllImport("sndfile.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sf_close(IntPtr sndfile);

        [DllImport("sndfile.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sf_strerror(IntPtr sndfile);

        private IntPtr _handle;
        private readonly WaveFormat _format;
        private readonly long _totalFrames;
        private readonly int _channels;
        private float[] _floatBuf = new float[0];
        private long _positionFrames;

        /// <summary>原始文件的位深（sndfile 的 format 高位）；0 表示未知，按 32 float 处理。</summary>
        public int SourceBits { get; private set; }

        /// <summary>原始容器/编码描述，例如 "FLAC"、"WAV (PCM 24)"。</summary>
        public string SourceDescription { get; private set; }

        public SndFileReader(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("文件不存在", path);

            var info = new SF_INFO();
            _handle = sf_open(path, SFM_READ, ref info);

            if (_handle == IntPtr.Zero)
            {
                string err = "不支持的音频格式";
                try
                {
                    IntPtr p = sf_strerror(IntPtr.Zero);
                    if (p != IntPtr.Zero) err = Marshal.PtrToStringAnsi(p) ?? err;
                }
                catch { }
                throw new InvalidDataException(err);
            }

            if (info.channels <= 0 || info.samplerate <= 0)
            {
                sf_close(_handle);
                _handle = IntPtr.Zero;
                throw new InvalidDataException("音频文件参数异常（声道数或采样率无效）");
            }

            _channels = info.channels;
            _totalFrames = info.frames > 0 ? info.frames : 0;
            _format = WaveFormat.CreateIeeeFloatWaveFormat(info.samplerate, _channels);

            ParseFormat(info.format);
        }

        /// <summary>
        /// SF_INFO.format 的高 16 位是子格式（PCM_16 / PCM_24 / FLOAT / VORBIS 等）。
        /// 这里只翻译出位深和可读名字，供界面显示。
        /// </summary>
        private void ParseFormat(int format)
        {
            // SF_INFO.format 的布局：高 16 位是主格式（0x170000 = FLAC），
            // 低 16 位是子格式（0x0002 = PCM_16，0x0006 = FLOAT）。
            int major = format & unchecked((int)0xFFFF0000);
            int sub = format & 0xFFFF;

            string majorName;
            switch (major)
            {
                case 0x010000: majorName = "WAV"; break;
                case 0x020000: majorName = "AIFF"; break;
                case 0x030000: majorName = "AU"; break;
                case 0x040000: majorName = "RAW"; break;
                case 0x050000: majorName = "PAF"; break;
                case 0x060000: majorName = "SVX"; break;
                case 0x070000: majorName = "NIST"; break;
                case 0x080000: majorName = "VOC"; break;
                case 0x0A0000: majorName = "IRCAM"; break;
                case 0x0B0000: majorName = "W64"; break;
                case 0x0C0000: majorName = "MAT4"; break;
                case 0x0D0000: majorName = "MAT5"; break;
                case 0x0E0000: majorName = "PVF"; break;
                case 0x0F0000: majorName = "XI"; break;
                case 0x100000: majorName = "HTK"; break;
                case 0x110000: majorName = "SDS"; break;
                case 0x120000: majorName = "AVR"; break;
                case 0x130000: majorName = "WAVEX"; break;
                case 0x160000: majorName = "SD2"; break;
                case 0x170000: majorName = "FLAC"; break;
                case 0x180000: majorName = "CAF"; break;
                case 0x190000: majorName = "WVE"; break;
                case 0x200000: majorName = "OGG"; break;
                case 0x230000: majorName = "MPEG"; break;
                default: majorName = "音频"; break;
            }

            // 子格式 → 位深（无法确定的返回 0）
            // 常量取自 libsndfile 官方 sndfile.h：
            //   PCM_S8=1 PCM_16=2 PCM_24=3 PCM_32=4 PCM_U8=5 FLOAT=6 DOUBLE=7
            //   ULAW=0x10 ALAW=0x11 IMA_ADPCM=0x12 MS_ADPCM=0x13 GSM610=0x20
            // 注意没有 0x0008 这个值 —— 老代码把它当 ULAW，导致后面整体错位一位。
            switch (sub)
            {
                case 0x0001: SourceBits = 8; break;      // PCM_S8
                case 0x0002: SourceBits = 16; break;     // PCM_16
                case 0x0003: SourceBits = 24; break;     // PCM_24
                case 0x0004: SourceBits = 32; break;     // PCM_32
                case 0x0005: SourceBits = 8; break;      // PCM_U8
                case 0x0006: SourceBits = 32; break;     // FLOAT
                case 0x0007: SourceBits = 64; break;     // DOUBLE
                case 0x0010: SourceBits = 8; break;      // ULAW
                case 0x0011: SourceBits = 8; break;      // ALAW
                case 0x0012: SourceBits = 4; break;      // IMA_ADPCM
                case 0x0013: SourceBits = 4; break;      // MS_ADPCM
                case 0x0020: SourceBits = 0; break;      // GSM610（压缩）
                case 0x0060: SourceBits = 0; break;      // VORBIS（压缩）
                case 0x0064: SourceBits = 0; break;      // OPUS（压缩）
                default: SourceBits = 0; break;
            }

            SourceDescription = SourceBits > 0
                ? string.Format("{0} · {1} bit", majorName, SourceBits)
                : majorName;
        }

        public override WaveFormat WaveFormat { get { return _format; } }

        public override long Length
        {
            get { return _totalFrames * _format.BlockAlign; }
        }

        public override long Position
        {
            get { return _positionFrames * _format.BlockAlign; }
            set
            {
                long frame = value / _format.BlockAlign;
                if (frame < 0) frame = 0;
                if (frame > _totalFrames) frame = _totalFrames;
                if (_handle != IntPtr.Zero) sf_seek(_handle, frame, SEEK_SET);
                _positionFrames = frame;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_handle == IntPtr.Zero || count <= 0) return 0;

            int blockAlign = _format.BlockAlign;
            int frames = count / blockAlign;
            if (frames <= 0) return 0;

            int need = frames * _channels;
            if (_floatBuf.Length < need) _floatBuf = new float[need];

            long got;
            try { got = sf_readf_float(_handle, _floatBuf, frames); }
            catch { return 0; }

            if (got <= 0) return 0;

            int samples = (int)got * _channels;
            Buffer.BlockCopy(_floatBuf, 0, buffer, offset, samples * 4);
            _positionFrames += got;
            return samples * 4;
        }

        protected override void Dispose(bool disposing)
        {
            if (_handle != IntPtr.Zero)
            {
                try { sf_close(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
            base.Dispose(disposing);
        }
    }
}
