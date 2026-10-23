# 参与贡献

感谢你愿意帮 RecLite 变得更好。提任何东西之前，先看一眼下面的规则就够。

## 报告问题

在 [Issues](https://github.com/WhichWitchWin/RecLite/issues) 里开一条，请带上：

- RecLite 版本（下载页上的版本号；或右键 exe → 属性 → 详细信息 → 产品版本，形如 `0.2.2+c109037`）
- Windows 版本（如 `Windows 11 23H2`）
- 涉及的音频设备型号与驱动类型（WASAPI 共享/独占；不支持 ASIO）
- 现象与复现步骤；能录屏更好
- 程序目录下 `RecLite.log` 里相关的几行（**先自行检查，别把含个人路径的整份日志贴上来**）

安全相关问题**不要**开 Issue，见 [SECURITY.md](SECURITY.md)。

## 提交代码

1. Fork 仓库，从 `main` 拉分支，分支名用 `fix/简短描述` 或 `feat/简短描述`
2. 提交信息格式：`<type>: <一句话说明>`，`type` 取 `feat` / `fix` / `docs` / `chore` / `ci`
3. 一个 PR 只做一件事；行为变化要在描述里写清「改之前 / 改之后」
4. 提 PR 前确认本地编译通过（见下）

`main` 已开启分支保护：直接推 `main` 会被拒绝，改动请走 PR，且 PR 必须通过 CI 检查（`编译检查（net48 / x64）`）。

## 本地开发

需要 **.NET SDK 8**（用来编译 `net48` 目标）与 Windows。

```powershell
dotnet build -c Release src/RecLite.csproj   # 产物 src\bin\Release\RecLite.exe
.\scripts\build.ps1                          # 产物固定到 out\RecLite.exe，日常运行用这份
.\scripts\smoke.ps1                          # 冒烟脚本（会启动程序做界面检查）
```

回归脚本在 `scripts\verify\`，每个脚本对应一个曾经出过问题的功能点，改动相关功能时请跑一遍。
其中 `verify-trim-bits.ps1` 只做反射调用与 WAV 读写，不需要真实声卡：

```powershell
.\scripts\verify\verify-trim-bits.ps1
```

## 不要提交

`bin/`、`obj/`、`out/`、`dist/`、`Recordings/`、`settings.json`、`*.log`、`smoke-artifacts/`、以及任何个人环境的绝对路径。
`.gitignore` 已经覆盖这些，别用 `-f` 强行加入。

## 代码风格

- 跟随所在文件的既有写法，不重排、不顺手格式化无关代码
- 注释写「为什么这么做」，不写「这一行做了什么」
- 目标框架固定 `net48`，只用 .NET Framework 4.8 可用的 API；平台固定 x64
- 界面文案保持中文与现有语气一致
- PowerShell 脚本必须存成 **UTF-8 with BOM**（PowerShell 5.1 无 BOM 会按 ANSI 读，中文全乱）

## 发布新版本（维护者）

发版只有三步，其余交给 CI：

1. **升版本号**：改 `src/RecLite.csproj` 的 `<Version>`。它必须与 tag 一致，否则 Release 工作流会直接失败。
2. **写更新日志**：在 `CHANGELOG.md` 顶部加 `## v<版本> — <一句话>`，下面按 `### 新增` / `### 修复` / `### 变更` / `### 移除` 分类，一条一行。
3. **打标签**：`git tag v<版本>` 然后 `git push origin v<版本>`。

`.github/workflows/release.yml` 随后自动：编译 → 组附件（`RecLite.exe`、`RecLite-UserGuide-zh.txt`、`SHA256SUMS.txt`）→ 建**草稿** Release → 校验附件齐全且哈希与本次构建一致 → 转正。
任何一步失败都会红，且草稿不会对外可见。

想预先看 Release 正文：`.\scripts\release-notes.ps1 -Version <版本>`。

补发已有版本：Actions 页面 → Release → Run workflow，填版本号即可（会覆盖附件）。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。你提交的代码默认以同一许可证发布。
