# RecLite 启动优化设计（D + A + B）

日期：2026-10-24　状态：设计定稿，实现暂缓　范围：只改启动时序与启动计量，不改录音行为

> 暂缓原因：启动慢目前尚未成为真实痛点。等它真的碍事了，按本文实施即可，不需要重新设计。

## 一、要解决的问题与实测基线

用户反馈：从双击图标到界面出现"有点长"。

同一台机（VMware / 集成声卡 / `out\RecLite.exe` v0.2.2）连跑 3 次的实测：

| 次数 | 双击 → 窗口出现 | 双击 → 界面可用 |
|---|---|---|
| 1（冷） | 1313 ms | 1667 ms |
| 2（热） | 370 ms | 586 ms |
| 3（热） | 305 ms | 563 ms |

日志里的启动时间线（同一次启动）：

```
19:33:25.573  启动
19:33:25.691  [采集] 已打开：缓冲 30 ms     ← 设备枚举 + 打开约 118 ms
19:33:25.895  [卡顿] 帧间隔 155 ms          ← 首帧渲染卡顿
```

**目标**：冷启动"到出窗口"降到 600 ms 上下，热启动降到 200 ms 上下；不改变任何录音行为与产物格式。

## 二、现状：窗口出现前做了什么

`App.OnStartup` → `new MainWindow()` → `win.Show()`。构造函数里的顺序是：

| 顺序 | 工作 | 成本 | 能否延后 |
|---|---|---|---|
| 1 | `InitializeComponent()` 解析 35 KB XAML | 大头（冷启动尤甚） | 否，窗口本体 |
| 2 | 挂事件、时间轴/dB 刻度 `Attach` | 很小 | 否 |
| 3 | `AppSettings.Load()` 读 `settings.json` | 很小 | 否，后面都要用 |
| 4 | 恢复窗口位置/大小 | 很小 | 否，必须在 Show 前 |
| 5 | 确保 `%TEMP%\WPF` 存在 | 很小 | 否 |
| 6 | `new RecordingEngine()` | 很小 | 否 |
| 7 | `CleanupStaleTemp()` 扫录音目录删 10 分钟前的 `*.tmp.wav` | 磁盘 I/O，目录大时不可控 | **可以** |
| 8 | `BuildOptionLists()` 采样率/格式/预录选项 | 小 | 否 |
| 9 | `LoadDevices()` 枚举录音源 + 播放设备（MMDevice ×2） | 中（专业声卡更久） | **可以** |
| 10 | `ApplySettingsToUi()` 尾部 `OpenSelectedDevice()`：打开 WASAPI 采集 + 启动预录缓存 | **约 118 ms** | **可以** |
| 11 | 起 16 ms UI 定时器 | 很小 | 否 |

结论：第 7、9、10 三段在"窗口出现"之前做完了，而它们**没有任何一项是画出窗口所必需的**。

## 三、方案

### D：先装一把尺子（必做，无风险）

新增 `src/Core/StartupTrace.cs`（静态类，两处使用 → 按 D8 抽成共享单元）：

- `StartupTrace.Begin()` 在 `App` 静态构造函数**第一行**调用，记下"进程启动 → 此刻"的耗时基线
  （`DateTime.Now - Process.GetCurrentProcess().StartTime`，精度约 15 ms，够用）并启动 `Stopwatch`。
- `StartupTrace.Mark("窗口可见")`、`StartupTrace.Mark("设备就绪")` 打点。
- `StartupTrace.Flush()` 在"设备就绪"后往 `RecLite.log` 写**一行**汇总：
  `[启动] 进程→窗口 312 ms，窗口→设备就绪 132 ms，合计 444 ms`
- 全部包在 `try/catch` 里：计量坏掉绝不能影响启动。

验收：同一套测量脚本（外部计时）与日志内计时能对上，误差 < 10%。

### A：先开门、后备菜

**改动**：构造函数里保留第 1–6、8、11 步；把第 7、9、10 步挪到窗口第一帧画完之后。

```csharp
ContentRendered += (s, e) => Dispatcher.BeginInvoke(new Action(RunDeferredStartup),
    DispatcherPriority.Background);
```

`RunDeferredStartup()` 分两段，都在 UI 线程、但发生在窗口已可见之后：

1. **枚举**：`LoadDevices()` + `ApplySettingsToUi()` 里"选设备"的那部分
   —— 把现在的 `ApplySettingsToUi()` 拆成 `ApplyNonDeviceSettings()`（构造期跑，选项/音量/目录等）
   与 `ApplyDeviceSelection()`（延后期跑，依赖 `_sources`/`_playbacks`）。
2. **连接**：`OpenSelectedDevice()`（原来的第 10 步）。

`CleanupStaleTemp()` 直接放进延后期的第一段——纯清理，与界面无关。

**延后期间的用户可见状态**：

- 状态栏显示「正在连接设备…」
- `CaptureCombo` / `PlaybackCombo` 暂时为空、`IsEnabled = false`
- 「● 录音」按钮**禁用**（设备没连好时按下去只会报错）
- 设备就绪后：填好下拉框、恢复启用、状态栏回到正常文案
- 设备打开失败：沿用现有失败提示逻辑（状态栏红字 + 按钮保持禁用），不新增分支

**不改的**：单实例互斥、第二实例唤起路径（都在 `OnStartup`，早于窗口，不受影响）；
`--selftest` / `--no-tray` 分支不变。

### B：JIT 预热缓存

在 `StartupTrace.Begin()` 之后、其余代码之前：

```csharp
System.Runtime.ProfileOptimization.SetProfileRoot(ProfileDir);
System.Runtime.ProfileOptimization.StartProfile("startup.profile");
```

`ProfileDir` 取 `%TEMP%\RecLite\profile`（**不能**放程序目录：绿色版承诺"删掉文件夹即卸载干净"，
往程序目录写额外文件会破坏这一条）。整段 `try/catch` 静默失败。

预期：第 2 次及以后启动的 JIT 部分变快（通常 50–150 ms）；对**真正第一次**启动无效（那时还没生成 profile）。

## 四、非目标

- 不拆 `MainWindow.xaml`（35 KB）、不改控件结构
- 不改设备枚举实现、不改单文件内嵌/解包策略
- 不做"后台线程真正异步打开设备"——那需要处理 NAudio 与 UI 线程的交叉，风险高于收益，留作第二批
- 不做 ReadyToRun/NGen（net48 需改构建链，收益不确定）

## 五、风险与应对

| 风险 | 影响 | 应对 |
|---|---|---|
| **自动化脚本失败**：`smoke.ps1` 与 6 个 `verify-*.ps1` 会点按钮、等 UIA 后代；按钮暂时禁用或状态栏文案变化都可能让它们红 | 回归全线报警 | 改完必须逐个跑一遍（本机有 HD Audio 可以真跑）；脚本里若有"等按钮可用"的步骤不够稳，就在脚本侧补等待，不改产品行为 |
| 预录缓存晚约 150 ms 开始 | 开机后立刻录音会少不到 0.2 秒的预录 | 设备就绪前录音按钮禁用，用户按不到；且预录默认 15 秒，影响可忽略 |
| 延后期抛异常会让界面停在"正在连接设备…" | 用户看到空下拉框 | `RunDeferredStartup` 整体 `try/catch` + `Log.Error`，失败时状态栏明确写原因并保持按钮禁用 |
| `ProfileOptimization` 在受限环境写不了目录 | 启动报错 | 整段 `try/catch`，失败即跳过 |
| 计量代码本身拖慢启动 | 得不偿失 | 只有几个 `Stopwatch` 打点 + 一行日志，成本在微秒级 |

## 六、验证方式

1. **数字对比**：同一台机，重启后冷启动 3 次 + 连续热启动 3 次，用与基线**完全相同**的外部计时脚本测"窗口出现 / 界面就绪"，并与日志内 `[启动]` 行交叉核对。
2. **功能不回归**：`scripts\verify\verify-trim-bits.ps1`（无需声卡，CI 也跑）+ `scripts\smoke.ps1`（真启动、录音、试听）+ 其余 6 个 `verify-*.ps1`。
3. **CI**：编译 + 内嵌资源自足 + 裁剪矩阵回归全绿。
4. **边界手测**：设备就绪前点「● 录音」应无反应（按钮禁用）；把 `settings.json` 里设备 Id 改成不存在的设备，确认失败提示与禁用状态正确。

## 七、交付件

- 改：`src/App.xaml.cs`、`src/MainWindow.xaml.cs`
- 新增：`src/Core/StartupTrace.cs`
- 改：`CHANGELOG.md`（v0.2.3「变更：启动先出窗口再连接设备；新增启动耗时日志」）
- 版本号 0.2.2 → 0.2.3，按三步发版
