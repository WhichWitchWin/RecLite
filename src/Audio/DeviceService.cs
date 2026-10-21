using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace RecLite.Audio
{
    /// <summary>
    /// 设备枚举服务。基于 WASAPI（Windows Vista+ 的官方音频接口）。
    /// 与 Moo0 的老式 WINMM 不同：WASAPI 认「系统默认设备」，不依赖设备名里带 Microphone 字样。
    /// </summary>
    public static class DeviceService
    {
        /// <summary>
        /// 枚举可用录音源：
        /// 1) 所有录音设备（麦克风 / 声卡输入）
        /// 2) 所有播放设备的回环（录电脑正在播放的声音，无需「立体声混音」）
        /// </summary>
        public static List<AudioDeviceInfo> GetRecordingSources()
        {
            var list = new List<AudioDeviceInfo>();
            try
            {
                using (var en = new MMDeviceEnumerator())
                {
                    AddAll(en, DataFlow.Capture, DeviceKind.Capture, list);
                    AddAll(en, DataFlow.Render, DeviceKind.Loopback, list);
                }
            }
            catch (Exception)
            {
                // 音频服务异常时返回已收集到的部分，不让界面崩
            }
            return list;
        }

        /// <summary>枚举试听用的播放设备</summary>
        public static List<AudioDeviceInfo> GetPlaybackDevices()
        {
            var list = new List<AudioDeviceInfo>();
            try
            {
                using (var en = new MMDeviceEnumerator())
                    AddAll(en, DataFlow.Render, DeviceKind.Render, list);
            }
            catch (Exception) { }
            return list;
        }

        /// <summary>
        /// 枚举一路端点并追加描述。MMDevice 是 COM 包装，之前只靠 GC 收尾
        /// （.NET 的 RCW 终结器不保证及时跑），反复刷新设备列表会持续堆积 COM 引用；
        /// 现在描述完立刻释放。（MMDeviceCollection 本身在 NAudio 里没实现 IDisposable，
        /// 无法显式释放，只能靠它自己的实现收尾。）
        /// </summary>
        private static void AddAll(MMDeviceEnumerator en, DataFlow flow, DeviceKind kind,
                                   List<AudioDeviceInfo> list)
        {
            var col = en.EnumerateAudioEndPoints(flow, DeviceState.Active);
            foreach (var d in col)
            {
                try { list.Add(Describe(d, kind)); }
                finally { if (d != null) { try { d.Dispose(); } catch (Exception) { } } }
            }
        }

        /// <summary>按 Id 取 MMDevice；找不到返回 null（返回的对象归调用方释放）</summary>
        public static MMDevice FindById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            try
            {
                using (var en = new MMDeviceEnumerator())
                {
                    foreach (DataFlow flow in new[] { DataFlow.Capture, DataFlow.Render })
                    {
                        foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
                        {
                            // 命中就原样返回（所有权交给调用方）；
                            // 没命中的也必须释放，否则每次按 Id 找设备都漏一批端点。
                            if (string.Equals(d.ID, id, StringComparison.OrdinalIgnoreCase))
                                return d;
                            try { d.Dispose(); } catch (Exception) { }
                        }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>取系统默认录音设备（可能为 null）</summary>
        public static MMDevice GetDefaultCapture()
        {
            try
            {
                using (var en = new MMDeviceEnumerator())
                {
                    if (!en.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)) return null;
                    return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>取系统默认播放设备（可能为 null）</summary>
        public static MMDevice GetDefaultRender()
        {
            try
            {
                using (var en = new MMDeviceEnumerator())
                {
                    if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
                    return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
            }
            catch (Exception) { return null; }
        }

        private static AudioDeviceInfo Describe(MMDevice d, DeviceKind kind)
        {
            var info = new AudioDeviceInfo
            {
                Id = SafeGet(() => d.ID, ""),
                Name = SafeGet(() => d.FriendlyName, "(未知设备)"),
                Kind = kind
            };
            // d.AudioClient 每次访问都会 Activate 一个 IAudioClient COM 对象（NAudio 只在
            // MMDevice 内部缓存指针，不会自己 Release）。之前什么都不释放，每枚举一次设备
            // 就泄一批 COM 引用；现在读完 MixFormat 立刻释放。
            AudioClient ac = null;
            try
            {
                ac = d.AudioClient;
                var f = ac.MixFormat;
                info.SampleRate = f.SampleRate;
                info.Channels = f.Channels;
                info.BitsPerSample = f.BitsPerSample;
            }
            catch (Exception) { }
            finally
            {
                if (ac != null) { try { ac.Dispose(); } catch (Exception) { } }
            }
            return info;
        }

        private static T SafeGet<T>(Func<T> getter, T fallback)
        {
            try { return getter(); } catch (Exception) { return fallback; }
        }
    }
}
