**中文** ｜ [English](README.en.md)

# RecLite 轻录音

[![CI](https://github.com/WhichWitchWin/RecLite/actions/workflows/ci.yml/badge.svg)](https://github.com/WhichWitchWin/RecLite/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/WhichWitchWin/RecLite?label=release)](https://github.com/WhichWitchWin/RecLite/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-lightgrey)](https://github.com/WhichWitchWin/RecLite/releases/latest)

一个轻量的 Windows 录音工具：**单文件、绿色免安装、自带预录缓存**。
专治老式录音软件的「设备识别」毛病。

---

## 为什么做它

很多老录音软件（比如 Moo0 AudioRecorder）还在用 1990 年代的 WINMM 接口：

- 录麦克风要靠设备名里带 `Microphone`
- 录电脑声要靠「立体声混音」这个虚拟设备

结果在专业声卡（例如 RME）上就弹窗「你的麦克风已被禁用」。

RecLite 用的是 Windows 官方新接口 **WASAPI**：

- 录人声 = 直接选录音设备，不管它叫什么名字
- 录电脑声 = 直接选播放设备走「回环」，不需要立体声混音

从根源上不存在这类弹窗。

---

## 功能

**预录缓存（核心）**
后台一直在记着最近一段时间，你按下录音时这段会自动接到文件开头，
所以不会错过「刚才那句重要的话」。秒数可以自己填（`60`、`90`、`2分30秒` 都行），
上限随采样率变化，48kHz 时最长 600 秒。

**录制**
- 录制通道：任选录音设备，或任意播放设备的〔内录〕（录电脑发出的声音）
- 最多支持 2 声道，超过 2 声道的设备会被拒绝使用
- 录制音量 0~300%（只影响写进文件的内容，不影响你听到的声音）
- 暂停 / 继续：暂停期间不写盘、不计时，继续后仍是同一段录音
- 录音进行中不能切换通道、不能改预录秒数 —— 避免一个文件里混两种格式

**保存**
- 格式：WAV（16 / 24 / 32 位浮点）、MP3（128 / 192 / 256 / 320 kbps）
  MP3 依赖 Windows 自带编码器，精简系统上未安装对应组件时该选项不出现，仅保留 WAV
- 采样率：跟随设备，或 44.1k / 48k / 88.2k / 96k
- 文件名：自动时间戳命名，或停止时询问
- 目录：默认程序同目录的 `Recordings\`，可更改
- 停止方式（录音中当场选）：
  `■ 停止` 连开头的预录一起保存；
  `■ 停止（不含预录）` 只留你按下录音之后的音频；
  `■ 丢弃` 放弃这一段，不保存

**显示与试听**
- 实时波形 + 时间轴 + 秒数指示器（白=待机 / 绿=预录 / 黄=录音）
- 输入、输出双电平表 + 削波指示
- 波形支持滚轮缩放（Shift+滚轮纵向缩放）；试听时可在波形上拖动定位
- 试听：录完直接回放，也能打开或拖入任意音频（WAV / MP3 / FLAC / OGG / AIFF 等）

**托盘**
- 点「缩到托盘」把窗口收进通知区，录音照常继续；双击图标回到窗口，右键「显示主窗口 / 退出」
- 程序已经在跑时再双击一次 `RecLite.exe`，会把已有窗口叫回来，不会开出第二个

---

## 下载使用

[**⬇ 下载最新版 `RecLite.exe`**](https://github.com/WhichWitchWin/RecLite/releases/latest/download/RecLite.exe) —— 就这一个文件，双击即用。

Release 页同时提供：

- `RecLite-UserGuide-zh.txt` —— 中文详细操作说明（仓库里是 [`docs/user-guide-zh.txt`](docs/user-guide-zh.txt)）
- `SHA256SUMS.txt` —— 下载后可用于校验文件是否完整

系统要求：**Windows 10 1903+ / Windows 11**（系统自带 .NET Framework 4.8），
不需要安装任何运行时。

> 首次运行如果出现「Windows 已保护你的电脑」，点【更多信息】→【仍要运行】。
> 程序没有购买代码签名证书，这是「未知发布者」提示，不是病毒警告。
> 想让以后不再弹：右键 exe → 属性 → 底部勾选「解除锁定」→ 确定。

绿色特性：

- 设置存在同目录 `settings.json`，录音存在同目录 `Recordings\`
- 不写注册表、不写 AppData
- 删掉整个文件夹就等于卸载干净

> 系统临时目录里会留两样东西，都不是用户数据，可以随手删掉：
> `%TEMP%\RecLite\<版本号>\` —— 单文件版解包出的解码库，约 2.5MB，**按版本各留一份**；
> `%TEMP%\WPF` —— WPF 框架自己的渲染临时目录。
> 原生库没法从内存直接加载，这是单文件化的妥协。

---

## 从源码构建（可选）

**普通用户不需要构建** —— 直接从 [Releases](https://github.com/WhichWitchWin/RecLite/releases/latest) 页下载成品即可。

想自己编译的话（需要 Windows 与 .NET SDK 8）：

```powershell
dotnet build -c Release src/RecLite.csproj   # 产物 src\bin\Release\RecLite.exe
.\scripts\build.ps1                          # 或者用它，产物固定到 out\RecLite.exe
```

产物是**单文件 exe**：所有托管依赖和原生 `sndfile.dll` 都已内嵌，
运行时由 `src/Core/Bundled.cs` 解析（托管）或解包（原生），所以只需要复制这一个文件。

冒烟与回归脚本：`scripts\smoke.ps1`（启动程序做界面自检）、`scripts\verify\`（按功能点划分的验证脚本）。

---

## 目录结构

```
.github/             CI、自动发版工作流、Issue / PR 模板
docs/                用户文档（中文使用说明）
licenses/            第三方组件许可证全文
scripts/             构建（build.ps1）、冒烟与回归脚本
src/                 源代码（.NET Framework 4.8 + WPF，x64）
  Audio/             采集、编码、预录缓存、设备枚举
  Core/              设置、日志、路径、内嵌程序集加载
  UI/                波形、时间轴、电平表等自绘控件
  lib/               sndfile.dll，以及打包时内嵌用的托管依赖
  app.ico            程序图标（16~256 多尺寸）
out/                 本地构建产物（不入库）
```

---

## 已知限制

- 设备级通道选择（如 "ADAT (3+4)"），暂不支持拆分单根物理声道
- 最多 2 声道
- MP3 只支持 32 / 44.1 / 48 kHz，其他采样率会自动转换
- 预录缓存占内存：48kHz 立体声约 23 MB / 分钟（缓存 600 秒约 230 MB）
- 时间轴最多显示约 4 分 17 秒（屏幕宽度决定的几何极限）

---

## 第三方依赖

| 组件 | 用途 | 许可证 |
|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) 2.2.1 | WASAPI 采集与播放、MP3 编码 | MIT |
| [libsndfile](https://github.com/libsndfile/libsndfile) 1.2.2 | 文件解码（FLAC / OGG / AIFF 等） | LGPL-2.1 |

两者均以二进制形式内嵌在 `RecLite.exe` 中。许可证全文在 [`licenses/`](licenses/)，
合规说明（含 LGPL 要求的「可替换库」做法）见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

---

## 许可证

本项目使用 [MIT 许可证](LICENSE)。

第三方组件另有各自许可证：NAudio 为 MIT，libsndfile 为 LGPL-2.1
（全文在 [`licenses/`](licenses/)，合规说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)）。

版本变化见 [CHANGELOG.md](CHANGELOG.md)。想参与改进见 [CONTRIBUTING.md](CONTRIBUTING.md)；安全问题请按 [SECURITY.md](SECURITY.md) 私密报告，不要开公开 Issue。
