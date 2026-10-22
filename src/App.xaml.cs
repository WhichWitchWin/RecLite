using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using RecLite.Core;

namespace RecLite
{
    public partial class App : Application
    {
        // 单实例 + 「再双击一次 exe 就把托盘里的窗口叫回来」。
        // 为什么必须有：托盘图标可能被系统藏进「隐藏的图标」溢出区，甚至根本画不出来
        // （.NET 的 NotifyIcon 不回报成败），而窗口一旦 Hide() 就没了任何入口。
        // 全局热键在这台机器上被系统层拦掉（RegisterHotKey 一律失败），所以入口用最土但最可靠的一招：
        // 用户再双击一次程序，第二个实例把第一个叫回来，然后自己安静退出。
        private const string InstanceMutexName = "RecLite.SingleInstance.6E0B3D31";
        private const string RestoreEventName = "RecLite.RestoreRequest.6E0B3D31";

        private static Mutex _instanceMutex;
        private static EventWaitHandle _restoreEvent;
        private static RecLite.MainWindow _mainWindow;   // 监听线程只读这个引用，不碰 Application.MainWindow

        /// <summary>
        /// 必须比「任何 NAudio 类型第一次被碰到」更早执行。
        /// 静态构造在 new App() 时就会跑，早于 InitializeComponent 和 OnStartup，
        /// 所以把内嵌程序集解析器装在这里最稳。
        /// </summary>
        static App()
        {
            Bundled.Install();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 幂等，重复调用没代价；兜一层防止静态构造被裁剪或顺序变化
            Bundled.Install();

            // 自检模式：RecLite.exe --selftest [日志路径]
            var args = e.Args ?? new string[0];
            int idx = Array.IndexOf(args, "--selftest");
            if (idx >= 0)
            {
                string log = (idx + 1 < args.Length && !args[idx + 1].StartsWith("-"))
                    ? args[idx + 1]
                    : Path.Combine(AppPaths.BaseDir, "selftest.log");
                try { SelfTest.Run(log); } catch { }
                Shutdown(0);
                return;
            }

            // 自动化脚本（冒烟 / 专项回归都带 --no-tray）不参与单实例：
            // 否则脚本一启动就会被用户开着的那个实例顶回去，测试直接失败。
            bool automation = Array.IndexOf(args, "--no-tray") >= 0;
            if (!automation && !TryBecomePrimaryInstance())
            {
                // 已经有一个 RecLite 在跑（大概缩在托盘里）：叫它把窗口显示出来，自己退出
                RequestRestoreOfExisting();
                Shutdown(0);
                return;
            }

            // 全局异常兜底：避免静默崩溃
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, a) =>
            {
                var ex = a.ExceptionObject as Exception;
                // 必须先落日志再弹窗 —— 原来两个兜底都只弹窗不写日志，
                // 于是崩溃之后日志里一个字都没有，完全无从查起。
                try { Log.Error("AppDomain.UnhandledException", ex); } catch { }
                MessageBox.Show("发生未处理的错误：\n" + (ex?.Message ?? "未知错误"),
                    "RecLite", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            var win = new MainWindow();
            MainWindow = win;
            _mainWindow = win;
            win.Show();
        }

        /// <summary>
        /// 抢「第一个实例」的身份；抢到就顺手起一个后台线程，等别人来敲门（再双击 exe）。
        /// 返回 false = 已经有一个实例在跑。
        /// </summary>
        private static bool TryBecomePrimaryInstance()
        {
            try
            {
                bool createdNew;
                _instanceMutex = new Mutex(true, InstanceMutexName, out createdNew);
                if (!createdNew)
                {
                    try { _instanceMutex.Dispose(); } catch { }
                    _instanceMutex = null;
                    return false;
                }
            }
            catch (Exception ex)
            {
                // 互斥体都拿不到就别拦着用户用程序
                Log.Error("SingleInstance", ex);
                return true;
            }

            try
            {
                _restoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RestoreEventName);
                var th = new Thread(() =>
                {
                    while (true)
                    {
                        try
                        {
                            _restoreEvent.WaitOne();
                            // 跨线程不能碰 Application.MainWindow：它有线程亲和性，
                            // 在后台线程读会抛 InvalidOperationException。用自己存的静态引用。
                            var mw = _mainWindow;
                            if (mw != null)
                                mw.Dispatcher.BeginInvoke(new Action(() => mw.RestoreFromTray()));
                        }
                        catch (Exception ex) { Log.Error("RestoreListener", ex); }
                    }
                })
                { IsBackground = true, Name = "RecLite.RestoreListener" };
                th.Start();
            }
            catch (Exception ex) { Log.Error("RestoreEvent", ex); }

            return true;
        }

        /// <summary>第二实例：给正在跑的那个发「把窗口叫回来」的信号</summary>
        private static void RequestRestoreOfExisting()
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(RestoreEventName))
                    ev.Set();
                Log.Write("检测到已有实例，已请求它恢复窗口");
            }
            catch (Exception ex)
            {
                Log.Write("请求恢复已有实例失败: " + ex.Message);
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                // 同上：先写日志，再弹窗
                try { Log.Error("Dispatcher.UnhandledException", e.Exception); } catch { }
                MessageBox.Show("发生错误：\n" + e.Exception.Message,
                    "RecLite", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            }
            catch { }
        }
    }
}
