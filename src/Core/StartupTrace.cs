using System;
using System.Diagnostics;
using System.Text;

namespace RecLite.Core
{
    /// <summary>
    /// 启动耗时计量：把「双击 → 窗口可见 → 设备就绪」三段写进 RecLite.log。
    ///
    /// 为什么要有它：启动优化只能靠数字判断有没有效，靠「感觉快了」必然自欺。
    /// 全程 try/catch —— 计量代码自己出错绝不能影响启动，这是硬要求。
    /// </summary>
    public static class StartupTrace
    {
        private static Stopwatch _sw;

        // 「进程启动 → 代码开始跑」那段（运行时加载 exe、JIT 基础类库）用进程启动时间反算。
        // 只靠 Stopwatch 会漏掉这一段，而冷启动里它恰恰占大头。
        private static double _toCodeStartMs;

        private static double _windowTotalMs = -1;
        private static double _deviceTotalMs = -1;
        private static bool _flushed;

        /// <summary>在 App 静态构造函数第一行调用。</summary>
        public static void Begin()
        {
            try
            {
                double toNow = 0;
                try
                {
                    toNow = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
                    // 系统时钟被改过、或进程信息拿不到时会给出离谱值，直接丢弃
                    if (toNow < 0 || toNow > 60000) toNow = 0;
                }
                catch { }

                _toCodeStartMs = toNow;
                _sw = Stopwatch.StartNew();
            }
            catch { }
        }

        /// <summary>窗口第一帧真正画出来之后调用（只记第一次）。</summary>
        public static void MarkWindowVisible()
        {
            try
            {
                if (_sw == null || _windowTotalMs >= 0) return;
                _windowTotalMs = _toCodeStartMs + _sw.Elapsed.TotalMilliseconds;
            }
            catch { }
        }

        /// <summary>采集设备打开（或确定失败）之后调用（只记第一次）。</summary>
        public static void MarkDeviceReady()
        {
            try
            {
                if (_sw == null || _deviceTotalMs >= 0) return;
                _deviceTotalMs = _toCodeStartMs + _sw.Elapsed.TotalMilliseconds;
            }
            catch { }
        }

        /// <summary>
        /// 把三段耗时写成一行日志。幂等：重复调用（例如延后期写过、关窗时又补一次）只留一行。
        /// </summary>
        public static void Flush()
        {
            try
            {
                if (_sw == null || _flushed) return;
                _flushed = true;

                var sb = new StringBuilder("[启动] ");
                if (_windowTotalMs >= 0)
                    sb.AppendFormat("进程→窗口 {0:N0} ms，", _windowTotalMs);
                else
                    sb.Append("进程→窗口（未打点），");

                if (_deviceTotalMs >= 0)
                {
                    sb.AppendFormat("窗口→设备就绪 {0:N0} ms，", Math.Max(0, _deviceTotalMs - _windowTotalMs));
                    sb.AppendFormat("合计 {0:N0} ms", _deviceTotalMs);
                }
                else
                {
                    // 设备还没就绪就退出了：不报"合计"，否则会把它算成整个运行时长，误导后来人
                    sb.Append("设备就绪（未完成就退出）");
                }

                Log.Write(sb.ToString());
            }
            catch { }
        }
    }
}
