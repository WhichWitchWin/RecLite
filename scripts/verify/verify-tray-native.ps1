# 托盘图标「真的进了通知区」验证脚本
# 判据有两条，且必须同时成立：
#   ① 程序日志里有「托盘图标已登记（NIM_ADD 成功」；
#   ② 独立向 Shell 反查 GetRect 返回 hr == 0。
# 本机 GetRect 已知假阴性（见项目档案），所以在这台机器上很可能只出 INCONCLUSIVE ——
# 那表示「日志证据成立、缺独立反查」，不是功能回归。
# 注意：NotifyIcon 用 uID = 0、guidItem 全零登记（不是 1，也不是 GUID）。
# 本脚本必须用 UTF-8 with BOM 保存，否则 PowerShell 5.1 会把中文读成乱码。

$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
public struct NID { public int cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }

public class TrayFind {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("shell32.dll", SetLastError=true)] public static extern int Shell_NotifyIconGetRect(ref NID id, out RECT rect);

    public static List<KeyValuePair<IntPtr, string>> Windows(uint targetPid) {
        var list = new List<KeyValuePair<IntPtr, string>>();
        EnumWindows((h, l) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == targetPid) {
                var cls = new StringBuilder(256);
                GetClassName(h, cls, 256);
                var txt = new StringBuilder(256);
                GetWindowText(h, txt, 256);
                list.Add(new KeyValuePair<IntPtr, string>(h, cls + "|" + txt));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // NotifyIcon 固定用 uID = 0
    public static string RectOf(IntPtr hwnd, uint uid) {
        var nid = new NID();
        nid.cbSize = Marshal.SizeOf(typeof(NID));
        nid.hWnd = hwnd;
        nid.uID = uid;
        nid.guidItem = Guid.Empty;
        RECT r;
        int hr = Shell_NotifyIconGetRect(ref nid, out r);
        if (hr == 0) return string.Format("FOUND rect=({0},{1})-({2},{3})", r.Left, r.Top, r.Right, r.Bottom);
        return "hr=0x" + hr.ToString("X8");
    }
}
"@

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe  = Join-Path $root 'out\RecLite.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe，请先运行 build.cmd" }

$run  = Join-Path $env:TEMP ('RecLiteTrayNative\' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
Copy-Item $exe (Join-Path $run 'RecLite.exe') -Force

$proc = $null
try {
    # 关掉已有实例：用正常关闭，让 NIM_DELETE 跑完，别留残留图标
    Get-Process RecLite -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep -Milliseconds 1500
    Get-Process RecLite -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep -Milliseconds 800

    $proc = Start-Process -FilePath (Join-Path $run 'RecLite.exe') -WorkingDirectory $run -PassThru -WindowStyle Minimized
    Start-Sleep -Seconds 7

    # ---------- 证据 1：应用自己的日志 ----------
    $log = Join-Path $run 'RecLite.log'
    $text = ''
    if (Test-Path $log) { $text = [System.IO.File]::ReadAllText($log, [System.Text.Encoding]::UTF8) }
    # 断言文案必须与程序实际写的一致：v0.2.0 换托盘实现后，程序输出的是
    # 「托盘图标已登记（NIM_ADD 成功；…）」与「托盘图标登记失败（NIM_ADD 未成功；…）」。
    # 原来这里 grep 的两句在源码里根本不存在，导致本脚本永远只能吐 INCONCLUSIVE。
    $logOk  = $text -match '托盘图标已登记（NIM_ADD 成功'
    $logBad = $text -match '托盘图标登记失败（NIM_ADD 未成功'
    Write-Host ('log  : 自报已确认 = ' + $logOk + ' ; 自报未确认 = ' + $logBad)
    ($text -split "`r?`n") | Where-Object { $_ -match '托盘|Tray|GetRect' } | ForEach-Object { '  ' + $_ }

    # ---------- 证据 2：独立向 Shell 反查 ----------
    Write-Host '--- query shell for the icon rect (uid=0) ---'
    $found = $false
    foreach ($w in [TrayFind]::Windows([uint32]$proc.Id)) {
        $res = [TrayFind]::RectOf($w.Key, [uint32]0)
        if ($res.StartsWith('FOUND')) {
            Write-Host ('  hwnd=' + $w.Key + ' [' + $w.Value + '] -> ' + $res)
            $found = $true
        }
    }
    if (-not $found) { Write-Host '  no FOUND result for uid=0' }

    $verdict = if ($logOk -and $found) { 'PASS - 图标确实在通知区里' }
               elseif ($logBad -and -not $found) { 'FAIL - Shell 拒绝了登记（多为进程完整性级别过低）' }
               else { 'INCONCLUSIVE - 日志与反查不一致，需人工核对' }
    Write-Host ('RESULT: ' + $verdict)
}
finally {
    if ($proc -and -not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 1200 }
    if ($proc -and -not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null }
    Write-Host ('run dir: ' + $run)
}
