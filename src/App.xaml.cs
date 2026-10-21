using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using RecLite.Core;

namespace RecLite
{
    public partial class App : Application
    {
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
            win.Show();
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
