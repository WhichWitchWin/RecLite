using System;
using System.IO;
using NAudio.Wave;

namespace RecLite.Audio
{
    /// <summary>音频写入器接口（策略模式：WAV / MP3 / 以后可加 FLAC、OGG）</summary>
    public interface IAudioSink : IDisposable
    {
        /// <summary>实际写入的采样率</summary>
        int SampleRate { get; }
        /// <summary>实际写入的声道数</summary>
        int Channels { get; }
        /// <summary>已写入帧数</summary>
        long FramesWritten { get; }

        void Write(float[] interleaved, int frames);
        void Close();
    }

    /// <summary>
    /// WAV 写入器：把 32bit 浮点采样转为目标位深并落盘。
    /// 支持 16 / 24 / 32(float) bit。
    /// </summary>
    public class WavSink : IAudioSink
    {
        private WaveFileWriter _writer;
        private readonly int _bits;
        private readonly int _channels;
        private readonly int _sampleRate;
        private byte[] _buf = new byte[0];
        private long _frames;

        public int SampleRate => _sampleRate;
        public int Channels => _channels;
        public long FramesWritten => _frames;

        /// <param name="path">输出文件路径</param>
        /// <param name="sampleRate">采样率</param>
        /// <param name="channels">声道数</param>
        /// <param name="bits">位深 16 / 24 / 32(float)</param>
        public WavSink(string path, int sampleRate, int channels, int bits)
        {
            _sampleRate = Math.Max(8000, sampleRate);
            _channels = Math.Max(1, channels);
            _bits = bits == 16 ? 16 : bits == 24 ? 24 : 32;

            var fmt = _bits == 32
                ? WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, _channels)
                : new WaveFormat(_sampleRate, _bits, _channels);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _writer = new WaveFileWriter(path, fmt);
        }

        public void Write(float[] interleaved, int frames)
        {
            if (_writer == null || frames <= 0) return;
            int total = frames * _channels;
            int need = total * (_bits / 8);
            if (_buf.Length < need) _buf = new byte[need];

            int p = 0;
            for (int i = 0; i < total; i++)
            {
                float s = interleaved[i];

                // 32bit 浮点原样保留超过 ±1 的部分（增益拉过 100% 时的真实过载要能看见）；
                // 16/24bit 是定点格式，±1 就是物理边界，必须夹住。
                if (_bits != 32)
                {
                    if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                }

                if (_bits == 16)
                {
                    short v = (short)Math.Round(s * 32767f);
                    _buf[p++] = (byte)(v & 0xFF);
                    _buf[p++] = (byte)((v >> 8) & 0xFF);
                }
                else if (_bits == 24)
                {
                    int v = (int)Math.Round(s * 8388607f);
                    _buf[p++] = (byte)(v & 0xFF);
                    _buf[p++] = (byte)((v >> 8) & 0xFF);
                    _buf[p++] = (byte)((v >> 16) & 0xFF);
                }
                else
                {
                    // 32bit float 原样
                    var bytes = BitConverter.GetBytes(s);
                    _buf[p++] = bytes[0];
                    _buf[p++] = bytes[1];
                    _buf[p++] = bytes[2];
                    _buf[p++] = bytes[3];
                }
            }

            _writer.Write(_buf, 0, need);
            _frames += frames;
        }

        public void Close()
        {
            if (_writer != null)
            {
                // 这两个异常不能闷掉：磁盘满 / 句柄失效时 Flush 会失败，
                // 结果就是 WAV 头没写回去、文件看起来只有 0 秒，而用户以为存好了。
                try { _writer.Flush(); }
                catch (Exception ex) { Core.Log.Error("WavSink.Flush", ex); }

                try { _writer.Dispose(); }
                catch (Exception ex) { Core.Log.Error("WavSink.Dispose", ex); }

                _writer = null;
            }
        }

        public void Dispose() => Close();
    }
}
