using System;
using System.IO;

namespace RecLite.Core
{
    /// <summary>
    /// 绿色化路径管理：程序自己的数据（设置、日志、录音）一律落在程序目录内，
    /// 不写 AppData、不写注册表。
    ///
    /// 系统临时目录有两个例外，都不是程序数据：
    ///   1) 单文件发布解包原生库到 %TEMP%\RecLite\&lt;版本号&gt;\（见 Core/Bundled.cs）
    ///   2) WPF 自己的渲染临时目录 %TEMP%\WPF
    /// 删掉程序目录后这两个残留可以手动清理，不影响卸载完整性。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>程序所在目录（exe 同级）</summary>
        public static string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); }
        }

        /// <summary>配置文件路径</summary>
        public static string SettingsFile
        {
            get { return Path.Combine(BaseDir, "settings.json"); }
        }

        /// <summary>默认录音输出目录</summary>
        public static string DefaultRecordingsDir
        {
            get { return Path.Combine(BaseDir, "Recordings"); }
        }

        /// <summary>确保目录存在</summary>
        public static void EnsureDirectory(string dir)
        {
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        /// <summary>把相对路径解析为绝对路径（相对程序目录）</summary>
        public static string Resolve(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return BaseDir;
            return Path.IsPathRooted(path) ? path : Path.Combine(BaseDir, path);
        }
    }
}
