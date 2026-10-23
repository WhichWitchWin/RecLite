# 第三方组件声明 / Third-Party Notices

RecLite 以二进制形式内嵌下列第三方组件（最终都封装进 `RecLite.exe`）：

| 组件 | 版本 | 用途 | 许可证 | 许可证全文 |
|---|---|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) | 2.2.1 | WASAPI 采集与播放、MP3 编码 | MIT | [`licenses/NAudio-MIT.txt`](licenses/NAudio-MIT.txt) |
| [libsndfile](https://github.com/libsndfile/libsndfile) | 1.2.2 | 文件解码（FLAC / OGG / AIFF 等） | LGPL-2.1 | [`licenses/LGPL-2.1.txt`](licenses/LGPL-2.1.txt) |

## 关于 libsndfile（LGPL-2.1）

libsndfile 以**动态链接**方式使用，符合 LGPL-2.1 第 6 条关于「可替换库」的要求：

- 程序启动时优先加载 **exe 同目录下的 `sndfile.dll`**；只有该文件不存在时，才从 exe 内嵌资源解包到 `%TEMP%\RecLite\<版本号>\` 并使用。
- 因此任何人都可以用**自行编译或修改的 `sndfile.dll`**（文件名相同）放到 `RecLite.exe` 旁边来替换内置版本，无需修改或重新编译本程序。
- libsndfile 源码：<https://github.com/libsndfile/libsndfile>；许可证全文见 [`licenses/LGPL-2.1.txt`](licenses/LGPL-2.1.txt)。

## 关于 NAudio（MIT）

NAudio 采用 MIT 许可证，允许在保留版权声明与许可证声明的前提下再分发。

## 再分发要求

分发 `RecLite.exe` 时请一并附带本文件与 `licenses/` 目录下的许可证全文。
