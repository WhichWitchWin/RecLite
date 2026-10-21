using System;
using System.IO;

namespace RecLite.Core
{
    /// <summary>
    /// 极简日志。写在程序目录的 RecLite.log —— 绿色化，删除程序目录即一并清除。
    /// 只记录关键动作与异常，用于定位问题。
    /// </summary>
    public static class Log
    {
        private static readonly object _lock = new object();

        /// <summary>单个日志文件的上限。超了就轮转，不是清空。</summary>
        private const long MaxSize = 8 * 1024 * 1024; // 8 MB

        /// <summary>
        /// 距上次检查文件大小的写入条数。
        /// 每写一条都去 new FileInfo().Length 是白白多一次 IO，
        /// 攒够 200 条查一次完全够用（最多超限约 20KB 才轮转，无所谓）。
        /// 初值设成 199：程序一启动、写第一条时就先检查一次，
        /// 免得上次遗留的超大日志要等 200 条之后才轮转。
        /// </summary>
        private static int _sinceCheck = 199;

        public static string FilePath
        {
            get { return Path.Combine(AppPaths.BaseDir, "RecLite.log"); }
        }

        public static void Write(string message)
        {
            try
            {
                lock (_lock)
                {
                    var path = FilePath;

                    if (++_sinceCheck >= 200)
                    {
                        _sinceCheck = 0;
                        RotateIfNeeded(path);
                    }

                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch { }
        }

        /// <summary>
        /// 超过上限就轮转：RecLite.log → RecLite.log.1，然后从空的 RecLite.log 继续写。
        /// 只保留最近一份历史（上一份 .1 会被覆盖）。
        ///
        /// 【为什么不沿用原来的「清空」】原来超限时直接把文件清空，
        /// 出事前那一段日志会被一起抹掉 —— 而那恰恰是排查时最需要的部分
        /// （探针日志尤其如此，它就是靠「最后停在哪一行」来定位问题的）。
        /// </summary>
        private static void RotateIfNeeded(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length <= MaxSize) return;

                string old = path + ".1";
                if (File.Exists(old)) File.Delete(old);
                fi.MoveTo(old);
            }
            catch { }
        }

        public static void Error(string where, Exception ex)
        {
            Write("[错误] " + where + " -> " + (ex == null ? "null" : ex.GetType().Name + ": " + ex.Message));
            if (ex != null && ex.StackTrace != null)
            {
                var lines = ex.StackTrace.Split('\n');
                int n = Math.Min(4, lines.Length);
                for (int i = 0; i < n; i++) Write("        " + lines[i].Trim());
            }
        }
    }
}
