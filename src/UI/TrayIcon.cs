using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RecLite.UI
{
    /// <summary>
    /// 系统托盘图标。
    ///
    /// 实现路线：完全交给 WinForms 官方封装 <see cref="NotifyIcon"/> ——
    /// 它是微软对 Shell_NotifyIcon 的规范封装，内部会正确调用
    /// NIM_ADD / NIM_SETVERSION / NIM_MODIFY / NIM_DELETE，可见性状态也可读。
    /// 本类只补一件 NotifyIcon 不提供的服务：Explorer 重启（WM_TASKBARCREATED）
    /// 后重新登记，否则任务栏重建后图标会消失。
    ///
    /// 历史教训（2026-10-21）：曾一度弃用 NotifyIcon、全手写 Shell_NotifyIcon，
    /// 结果漏掉了规范要求的 NIM_SETVERSION（文档明确「必须在每次 NIM_ADD 时调用」），
    /// 图标被 Shell 按最老的 v0 语义处理 —— NIM_ADD 返回成功、GetRect 却把图标查在
    /// 通知区之外（隐藏区一侧），用户始终看不到。结论：不自己重造规范实现。
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private const int WM_TASKBARCREATED = 0x031E;

        // ---- 状态 ----
        private readonly System.Windows.Window _window;
        private readonly Action _exit;
        private readonly NotifyIcon _notify;
        private readonly ContextMenuStrip _menu;
        private readonly TaskbarRestartWindow _wnd;
        private bool _disposed;

        public TrayIcon(System.Windows.Window window, Action exit)
        {
            _window = window;
            _exit = exit;

            _menu = BuildMenu();
            _notify = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "RecLite 轻录音",
                ContextMenuStrip = _menu,
                Visible = false            // 由 Show() 显式打开，避免构造期就出现在通知区
            };
            _notify.DoubleClick += (s, e) => Restore();

            // Explorer 重启后通知区是全新的，官方 NotifyIcon 不会自己补登记
            _wnd = new TaskbarRestartWindow(this);
        }

        /// <summary>
        /// 图标是否真的登记进了通知区。
        ///
        /// 判据用 NotifyIcon 内部的 added 字段 —— 它就是 NIM_ADD 的返回值，
        /// Shell 收下了才是 true，这是唯一可信的信号。
        /// （曾试过 Shell_NotifyIconGetRect：本机实测它会「假阴性」，
        /// 图标明明在通知区、用户肉眼可见，它仍返回 0x80004005，
        /// 据此拦截会把「缩到托盘」变成永远点不动。）
        /// </summary>
        public bool IsVisibleInShell
        {
            get
            {
                if (_notify == null || _disposed) return false;
                return IsAddedToShell();
            }
        }

        /// <summary>读 NotifyIcon 内部的 added 字段：NIM_ADD 成功过就是 true</summary>
        private bool IsAddedToShell()
        {
            try
            {
                if (NotifyAddedField == null) return _notify != null && _notify.Visible;
                object v = NotifyAddedField.GetValue(_notify);
                return v is bool && (bool)v;
            }
            catch { return _notify != null && _notify.Visible; }
        }

        /// <summary>把图标放进通知区，返回真实结果</summary>
        public TrayShowResult Show()
        {
            try
            {
                if (_notify == null || _disposed) return TrayShowResult.NotRegistered;

                _notify.Visible = true;   // 内部会走 NIM_ADD / NIM_SETVERSION

                bool added = IsAddedToShell();
                var st = added ? TrayShowResult.Visible : TrayShowResult.NotRegistered;

                // GetRect 仅作诊断记录，不参与判断（见 IsVisibleInShell 的说明）
                int hr;
                var rectState = QueryState(out hr);

                if (added)
                {
                    RecLite.Core.Log.Write(string.Format(
                        "托盘图标已登记（NIM_ADD 成功；GetRect 诊断：{0} hr=0x{1:X8}，仅参考）",
                        rectState, hr));
                }
                else
                {
                    RecLite.Core.Log.Write(string.Format(
                        "托盘图标登记失败（NIM_ADD 未成功；GetRect 诊断：{0} hr=0x{1:X8}）",
                        rectState, hr));
                }

                return st;
            }
            catch (Exception ex)
            {
                RecLite.Core.Log.Error("Tray.Show", ex);
                return TrayShowResult.Unknown;
            }
        }

        /// <summary>托盘悬浮提示改成当前状态</summary>
        public void SetText(string text)
        {
            try
            {
                if (_notify == null) return;
                _notify.Text = Truncate(text, 63);   // 官方限制：最多 63 个字符
            }
            catch (Exception ex) { RecLite.Core.Log.Error("Tray.SetText", ex); }
        }

        /// <summary>恢复窗口：显示 → 还原 → 激活</summary>
        public void Restore()
        {
            try
            {
                // 窗口状态由主窗口统一管理（它的 RestoreFromTray 已经处理了
                // 「最小化前是普通还是最大化」），这里不重复记一份，避免两套状态打架。
                if (_window is RecLite.MainWindow)
                {
                    ((RecLite.MainWindow)_window).RestoreFromTray();
                    return;
                }

                if (!_window.IsVisible) _window.Show();
                if (_window.WindowState == System.Windows.WindowState.Minimized)
                    _window.WindowState = System.Windows.WindowState.Normal;
                _window.Activate();
            }
            catch (Exception ex) { RecLite.Core.Log.Error("Tray.Restore", ex); }
        }

        private void ShowMenu()
        {
            try { _menu.Show(Cursor.Position); }
            catch (Exception ex) { RecLite.Core.Log.Error("Tray.ShowMenu", ex); }
        }

        private void ReRegister()
        {
            try
            {
                if (_notify == null || _disposed) return;
                RecLite.Core.Log.Write("通知区重建（WM_TASKBARCREATED），重新登记托盘图标");
                _notify.Visible = false;
                _notify.Visible = true;

                RecLite.Core.Log.Write("重新登记结果：NIM_ADD 成功=" + IsAddedToShell());
            }
            catch (Exception ex) { RecLite.Core.Log.Error("Tray.ReRegister", ex); }
        }

        // ---- Shell_NotifyIconGetRect：仅作诊断，不参与判断 ----
        // 本机实测它会「假阴性」（图标在通知区却返回 0x80004005），
        // 所以真正的判据是 NotifyIcon.added 字段，见 IsAddedToShell()。

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NOTIFYICONIDENTIFIER
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        /// <summary>
        /// 反查结果的三种状态。必须区分「确定没有」和「查不出来」：
        /// 前者要拦住隐藏窗口，后者只是我们没手段验证，不能因此误判成失败。
        /// </summary>
        public enum TrayShowResult
        {
            Visible,       // Shell 确认图标在通知区
            NotRegistered, // Shell 明确查不到这个图标
            Unknown        // 拿不到句柄等，无法判定
        }

        // NotifyIcon 内部那个隐藏窗口的句柄不对内公开，但 GetRect 必须要它：
        // 传 IntPtr.Zero 时 Shell 匹配不到任何已登记图标，必然返回非 0（这是个坑，
        // 曾经因此把「图标明明在」误判成「没登记」，导致「缩到托盘」被一直拒绝）。
        // NotifyIcon 是 .NET Framework 里十几年没动过的老类型，字段名很稳定
        //（已实测：window / id 两个私有字段都在）。
        private static readonly System.Reflection.FieldInfo NotifyWindowField =
            typeof(NotifyIcon).GetField("window",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private static readonly System.Reflection.FieldInfo NotifyIdField =
            typeof(NotifyIcon).GetField("id",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // added = NIM_ADD 的返回值，Shell 收下图标才是 true。这是最可信的判据。
        private static readonly System.Reflection.FieldInfo NotifyAddedField =
            typeof(NotifyIcon).GetField("added",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        /// <summary>取 NotifyIcon 内部窗口句柄；取不到返回 Zero</summary>
        private IntPtr GetNotifyHandle()
        {
            try
            {
                if (NotifyWindowField == null) return IntPtr.Zero;
                var w = NotifyWindowField.GetValue(_notify) as NativeWindow;
                return w == null ? IntPtr.Zero : w.Handle;
            }
            catch (Exception ex)
            {
                RecLite.Core.Log.Error("Tray.GetNotifyHandle", ex);
                return IntPtr.Zero;
            }
        }

        /// <summary>取 NotifyIcon 登记时用的 uID（默认 0）</summary>
        private uint GetNotifyUid()
        {
            try
            {
                if (NotifyIdField == null) return 0;
                object v = NotifyIdField.GetValue(_notify);
                return v is int ? (uint)(int)v : 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 向 Shell 反查图标真实状态。
        /// 注意：本机实测 GetRect 会给出「假阴性」——图标明明在通知区、用户看得见，
        /// 它仍返回失败（0x80004005）。所以这个结果**只能当日志参考，不能用来拦截操作**。
        /// </summary>
        private TrayShowResult QueryState(out int hr)
        {
            hr = 0;
            try
            {
                IntPtr h = GetNotifyHandle();
                if (h == IntPtr.Zero)
                {
                    hr = -1;
                    return TrayShowResult.Unknown;
                }

                var id = new NOTIFYICONIDENTIFIER();
                id.cbSize = Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER));
                id.hWnd = h;              // 必须是 NIM_ADD 用的那个句柄
                id.uID = GetNotifyUid();  // NotifyIcon 固定用 0，但读真实值更稳
                id.guidItem = Guid.Empty;

                RECT r;
                hr = Shell_NotifyIconGetRect(ref id, out r);
                return hr == 0 ? TrayShowResult.Visible : TrayShowResult.NotRegistered;
            }
            catch (Exception ex)
            {
                RecLite.Core.Log.Error("Tray.QueryState", ex);
                hr = -1;
                return TrayShowResult.Unknown;
            }
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (s, e) => Restore());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) =>
            {
                if (_exit != null) _exit();
            });
            return menu;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { if (_notify != null) { _notify.Visible = false; _notify.Dispose(); } }
            catch (Exception ex) { RecLite.Core.Log.Error("Tray.Dispose", ex); }

            try { if (_wnd != null) _wnd.DestroyHandle(); } catch { }
            try { if (_menu != null) _menu.Dispose(); } catch { }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "RecLite 轻录音";
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                var res = System.Windows.Application.GetResourceStream(new Uri("app.ico", UriKind.Relative));
                if (res != null)
                {
                    using (var s = res.Stream)
                    using (var orig = new Icon(s))
                    {
                        // 通知区用小尺寸，高分屏下也不会糊
                        return new Icon(orig, SystemInformation.SmallIconSize);
                    }
                }
            }
            catch (Exception ex) { RecLite.Core.Log.Error("Tray.LoadIcon", ex); }

            return SystemIcons.Application;   // 共享静态对象，不随本类释放
        }

        /// <summary>只用来接 WM_TASKBARCREATED 的隐藏窗口</summary>
        private sealed class TaskbarRestartWindow : NativeWindow
        {
            private readonly TrayIcon _owner;

            public TaskbarRestartWindow(TrayIcon owner)
            {
                _owner = owner;
                try { CreateHandle(new CreateParams()); }
                catch (Exception ex) { RecLite.Core.Log.Error("Tray.CreateHandle", ex); }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_TASKBARCREATED) _owner.ReRegister();
                base.WndProc(ref m);
            }
        }
    }
}
