using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RecLite.Core
{
    /// <summary>
    /// 单文件发布支持。
    ///
    /// 编译时把所有托管依赖（NAudio 系列等）和原生 sndfile.dll 都塞进 exe 的内嵌资源里，
    /// 于是发布包只需要一个 RecLite.exe，用户目录里不会多出一堆 dll。
    ///
    /// 托管依赖：挂 AppDomain.AssemblyResolve，运行时按名字从内嵌资源里读出来 Assembly.Load。
    /// 原生依赖：这个没法从内存直接加载，只能先落到磁盘再 LoadLibrary。
    ///          注意这跟「绿色化，不写系统目录」的原设计有冲突 —— 为了满足单文件发布，
    ///          这里会往 %TEMP%\RecLite\&lt;版本号&gt;\ 放一个 sndfile.dll（约 2.5MB 缓存）。
    ///          如果 exe 旁边本来就有 sndfile.dll（开发机），则优先用那个，完全不碰临时目录。
    /// </summary>
    internal static class Bundled
    {
        /// <summary>内嵌资源名前缀，和 csproj 里的 LogicalName 约定一致</summary>
        private const string Prefix = "Bundled.";

        private static bool _installed;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectoryW(string lpPathName);

        /// <summary>安装内嵌程序集解析器并准备原生库。可重复调用。</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            try { AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve; }
            catch (Exception ex) { SafeLog("Bundled.Hook", ex); }

            try { PrepareNative(); }
            catch (Exception ex) { SafeLog("Bundled.PrepareNative", ex); }
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs e)
        {
            try
            {
                // e.Name 形如 "NAudio.Core, Version=2.2.1.0, Culture=neutral, PublicKeyToken=null"
                var simple = new AssemblyName(e.Name).Name;
                var asm = typeof(Bundled).Assembly;

                var data = ReadResource(asm, Prefix + simple + ".dll");
                if (data == null) return null;   // 不是我们内嵌的，交回给默认解析

                return Assembly.Load(data);
            }
            catch (Exception ex)
            {
                SafeLog("Bundled.Resolve(" + e.Name + ")", ex);
                return null;
            }
        }

        private static void PrepareNative()
        {
            // 开发/测试机上 exe 旁边本来就有 sndfile.dll，直接用，不必解包
            if (File.Exists(Path.Combine(AppPaths.BaseDir, "sndfile.dll"))) return;

            var asm = typeof(Bundled).Assembly;
            var bytes = ReadResource(asm, Prefix + "sndfile.dll");
            if (bytes == null) return;           // 没内嵌（开发构建），交给系统按常规找

            var dir = Path.Combine(Path.GetTempPath(), "RecLite",
                asm.GetName().Version.ToString());
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "sndfile.dll");
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            {
                // 先写临时名再改名：多个实例同时启动时不会读到写了一半的文件
                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                catch (IOException)
                {
                    // 另一个实例抢先写好了，用它的
                    try { File.Delete(tmp); } catch (Exception) { }
                }
            }

            // SetDllDirectory 让后续 DllImport("sndfile.dll") 能在该目录找到；
            // 再显式 LoadLibrary 一次，确保模块真的被映射进来。
            SetDllDirectoryW(dir);
            if (LoadLibraryW(path) == IntPtr.Zero)
                throw new InvalidOperationException("LoadLibrary 失败，错误码 " + Marshal.GetLastWin32Error());
        }

        private static byte[] ReadResource(Assembly asm, string name)
        {
            using (var s = asm.GetManifestResourceStream(name))
            {
                if (s == null) return null;
                var buf = new byte[s.Length];
                int off = 0;
                while (off < buf.Length)
                {
                    int n = s.Read(buf, off, buf.Length - off);
                    if (n <= 0) break;
                    off += n;
                }
                return buf;
            }
        }

        private static void SafeLog(string where, Exception ex)
        {
            try { Log.Error(where, ex); } catch (Exception) { }
        }
    }
}
