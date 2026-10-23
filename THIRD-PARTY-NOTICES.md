# 第三方组件声明 / Third-Party Notices

RecLite 以二进制形式分发下列第三方组件：

| 组件 | 版本 | 用途 | 许可证 | 许可证全文 |
|---|---|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) | 2.2.1 | WASAPI 采集与播放、MP3 编码 | MIT | [`licenses/NAudio-MIT.txt`](licenses/NAudio-MIT.txt) |
| [libsndfile](https://github.com/libsndfile/libsndfile) | 1.2.2 | 文件解码（FLAC / OGG / AIFF 等） | LGPL-2.1 | [`licenses/LGPL-2.1.txt`](licenses/LGPL-2.1.txt) |

## 关于 libsndfile（LGPL-2.1）

### 分发与加载方式

- libsndfile 以**独立 DLL** 形式随本程序分发，不是链接进 `RecLite.exe` 的代码。
- 程序启动时**优先加载 exe 同目录下的 `sndfile.dll`**；只有该文件不存在时，才把 exe 内嵌的那一份解包到 `%TEMP%\RecLite\<版本号>\` 再加载。
- 因此你可以用**自行编译或修改的同名 `sndfile.dll`** 放到 `RecLite.exe` 旁边替换它，无需修改或重新编译本程序。

### 对应源码

- 本项目分发的 libsndfile 版本为 **1.2.2**（xiph 官方 win64 构建）。
- 完整源码：<https://github.com/libsndfile/libsndfile>，对应标签 <https://github.com/libsndfile/libsndfile/releases/tag/1.2.2>。
- 如需与某次发布二进制**完全对应**的源码副本，可在仓库开 Issue 索取，维护者会提供。

### 声明位置

本程序的界面里没有独立的版权声明或「关于」窗口，第三方组件声明统一放在本文件与 [`licenses/`](licenses/) 目录。

## 关于 NAudio（MIT）

NAudio 采用 MIT 许可证，允许在保留版权声明与许可证声明的前提下再分发。
许可证全文见 [`licenses/NAudio-MIT.txt`](licenses/NAudio-MIT.txt)（版权人 Mark Heath）。

## 再分发要求

分发 `RecLite.exe` 时请一并附带本文件与 `licenses/` 目录下的许可证全文。
