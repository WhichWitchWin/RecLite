# 参与贡献

感谢你愿意帮 RecLite 变得更好。提任何东西之前，先看一眼下面的规则就够。

## 报告问题

在 [Issues](https://github.com/WhichWitchWin/RecLite/issues) 里开一条，请带上：

- RecLite 版本（程序标题栏或 Release 号）与下载来源
- Windows 版本（如 `Windows 11 23H2`）
- 涉及的音频设备型号与驱动类型（WASAPI 共享/独占、ASIO 不适用）
- 现象与复现步骤；能录屏更好
- 程序目录下 `RecLite.log` 里相关的几行（**先自行检查，别把含个人路径的整份日志贴上来**）

安全相关问题**不要**开 Issue，见 [SECURITY.md](SECURITY.md)。

## 提交代码

1. Fork 仓库，从 `main` 拉分支，分支名用 `fix/简短描述` 或 `feat/简短描述`
2. 提交信息格式：`<type>: <一句话说明>`，`type` 取 `feat` / `fix` / `docs` / `chore` / `ci`
3. 一个 PR 只做一件事；行为变化要在描述里写清「改之前 / 改之后」
4. 提 PR 前确认本地编译通过（见下）

## 本地开发

需要 **.NET SDK 8**（用来编译 `net48` 目标）与 Windows。

```powershell
dotnet build -c Release src/RecLite.csproj   # 产物 src\bin\Release\RecLite.exe
scripts\build.ps1                            # 产物 out\RecLite.exe，日常运行用这份
scripts\smoke.ps1                            # 冒烟脚本（会启动程序做界面检查）
```

测试脚本在 `scripts/verify/`，每个脚本对应一个曾经出过问题的功能点，改动相关功能时请跑一遍。

## 不要提交

`bin/`、`obj/`、`out/`、`dist/`、`Recordings/`、`settings.json`、`*.log`、以及任何个人环境的绝对路径。
`.gitignore` 已经覆盖这些，别用 `-f` 强行加入。

## 代码风格

- 跟随所在文件的既有写法，不重排、不顺手格式化无关代码
- 注释写「为什么这么做」，不写「这一行做了什么」
- 目标框架固定 `net48`，只用 .NET Framework 4.8 可用的 API；平台固定 x64
- 界面文案保持中文与现有语气一致

## 许可证

本项目采用 [MIT 许可证](LICENSE)。你提交的代码默认以同一许可证发布。
