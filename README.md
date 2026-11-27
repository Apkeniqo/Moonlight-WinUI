<div align="center">

# Moonlight For PC

**Windows 上的 Moonlight / Sunshine 串流客户端**
WinUI 3 界面 + 原生 Win32 渲染窗口

![version](https://img.shields.io/badge/version-1.1.42-blue)
![platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)
![runtime](https://img.shields.io/badge/runtime-.NET%208%20%2F%20WinUI%203-purple)
![license](https://img.shields.io/badge/license-GPLv3-green)

[下载安装包](https://github.com/Apkeniqo/Moonlight-WinUI/releases/latest) · [问题反馈](https://github.com/Apkeniqo/Moonlight-WinUI/issues) · [更新日志](https://github.com/Apkeniqo/Moonlight-WinUI/releases)

</div>

---

## 目录

- [简介](#简介)
- [功能特性](#功能特性)
- [下载与安装](#下载与安装)
- [快速开始](#快速开始)
- [系统要求](#系统要求)
- [从源码构建](#从源码构建)
- [项目结构](#项目结构)
- [协议扩展](#协议扩展)
- [已知限制](#已知限制)
- [路线图](#路线图)
- [许可证](#许可证)

## 简介

Moonlight For PC 是面向 Windows 的 Moonlight 客户端实现，用于连接运行
[Sunshine](https://github.com/LizardByte/Sunshine) 的主机进行低延迟串流。

- **界面层**：WinUI 3（Windows App SDK 1.7），原生 Mica / Acrylic 材质、系统标题栏深色适配、卡片式电脑与应用列表；
- **渲染层**：独立的原生 Win32 窗口承载视频（D3D11VA 硬解 + D3D11 纹理上屏），避免 XAML 合成开销，保证延迟与稳定性；
- **会话层**：复用 [`moonlight-common-c`](https://github.com/moonlight-stream/moonlight-common-c)，支持动态分辨率、剪贴板同步等扩展能力；
- **分发形式**：自包含可执行文件，已内置 Windows App SDK 运行时，安装后无需额外依赖。

## 功能特性

### 串流

| 能力 | 说明 |
|---|---|
| 视频解码 | H.264 / HEVC / AV1，D3D11VA 硬件解码 + 软件回退 |
| 音频 | Opus 解码 → WASAPI 共享模式输出 |
| 分辨率 | 运行时动态切换（`0x5506`），可单独调整码率而不重连 |
| 画面 | 保持宽高比、黑边填充窗口材质、窗口 / 全屏切换 |
| 输入 | 键盘、鼠标（绝对 / 相对两种模式、自动指针锁定、滚轮、灵敏度、左右键交换） |
| 光标 | 本地光标模式（主机推送形状）与视频内合成两种方式 |
| 剪贴板 | 文本与图片双向同步（`0x5508`） |
| 触控 | 多点触控上报（坐标按视频区归一化） |
| 性能浮窗 | FPS / 码率 / 网络延迟 / 丢包 / 解码耗时，位置与透明度可调 |
| 断线重连 | 主机退出或网络中断后自动重连（5 次），带遮罩进度提示 |

### 界面

- 四种窗口材质：Mica / Mica Alt / Acrylic / 纯色，主窗口与串流窗口保持一致；
- 系统标题栏 + 内容延伸，窗口边框跟随系统深色主题；
- 开屏过渡、页面切换动画（按 Windows 官方 Page Refresh 规范实现）；
- 电脑页与应用页统一的刷新按钮与在线状态探测；
- 配对流程（PIN + 客户端证书）、应用列表与封面、远程结束运行中的应用；
- Wake-on-LAN 远程唤醒。

### 安装与维护

- 自研深色分栏安装器：可自定义安装路径、已安装时自动带出原路径；
- 检测到程序运行中时提示是否关闭后继续；
- 分阶段安装进度（解压 / 注册表 / 快捷方式 / 卸载信息）；
- 完成后按钮切换为「打开 / 关闭」，无多余弹窗；
- 支持静默安装与命令行卸载（见下）。

## 下载与安装

前往 [Releases](https://github.com/Apkeniqo/Moonlight-WinUI/releases/latest) 下载
`MoonlightForPC-Setup-<version>.exe`，双击安装。

> 安装包为自签名证书，Windows SmartScreen 可能提示，选择「仍要运行」即可。

静默安装与卸载：

```powershell
# 静默安装到默认路径
MoonlightForPC-Setup-1.1.42.exe /silent

# 静默安装到指定路径
MoonlightForPC-Setup-1.1.42.exe /silent /dir="D:\Apps\Moonlight For PC"

# 卸载
& "C:\Program Files\Moonlight For PC\Uninstall.exe" /uninstall
```

## 快速开始

1. 在**主机**上安装并启动 Sunshine，添加需要串流的应用；
2. 打开 Moonlight For PC → 点击「配对电脑」→ 输入主机 IP；
3. 主机端 Sunshine 面板会显示 4 位 PIN，填入客户端完成配对；
4. 在电脑页确认主机在线 → 进入主机详情页 → 选择应用开始串流；
5. 串流中把鼠标移到画面右上角唤出齿轮菜单，或使用性能浮窗观察指标。

## 系统要求

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 10 1809+ / Windows 11（x64） |
| 显卡 | 支持 D3D11VA 硬解的 GPU（HEVC / AV1 需硬件支持） |
| 磁盘 | 约 200 MB |
| 网络 | 局域网千兆有线优先；无线建议 5GHz / Wi-Fi 6 |

客户端为自包含发布，**无需**安装 .NET 运行时或 Windows App SDK 运行时。

## 从源码构建

### 环境

- Visual Studio 2022+（.NET 桌面开发 + 使用 C++ 的桌面开发 + Windows SDK）
- .NET 8 SDK
- CMake（编译 `moonlight-common-c` 时使用，VS 安装目录自带）

### 1. 编译原生层

```bat
cd native
build-mlc.bat         :: moonlight-common-c + enet → native/mlc-build
build-native.bat      :: mlrender.dll（必须在上一步之后执行）
build-pairtls.bat     :: pairtls.dll（OpenSSL HTTPS 客户端）
```

### 2. 编译主程序

> 必须使用 Visual Studio 自带的 MSBuild；`dotnet build` 缺少 PriGen 任务，会报 `MSB4062`。

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
    -restore MoonlightWinUI.csproj /p:Configuration=Debug /p:Platform=x64
```

### 3. 生成安装包

```powershell
# 全量清理 → 编译 → 打包（自动读取 VERSION 文件）
powershell -ExecutionPolicy Bypass -File tools\package-release.ps1
```

产物输出到 `dist\MoonlightForPC-Setup-<version>.exe`。

## 项目结构

```
MoonlightWinUI (C# / WinUI 3)
├── MainWindow            主窗口：电脑列表、应用列表、设置、配对
├── StreamWindow          串流窗口：视频承载、HUD、控制面板
├── ControlWindow         无边框控制面板
├── VideoSurface          native 层互操作封装（P/Invoke）
├── HostApi / Pairing     主机 HTTP 接口与配对流程
├── HostStore             电脑列表持久化
└── native/
    ├── mlrender.cpp      D3D11 解码、纹理上屏、子窗口与输入
    ├── mlsession.cpp     moonlight-common-c 会话封装、扩展消息
    ├── mlaudio.cpp       Opus 解码 + WASAPI 播放
    ├── streamwin.cpp     Win32 串流窗口（标题栏、全屏、比例、材质、重连遮罩）
    ├── hud.cpp           性能浮层
    ├── perfcard.cpp      性能卡片浮窗
    ├── pairtls.cpp       OpenSSL HTTPS 请求（配对 / 剪贴板）
    └── third_party/      第三方依赖（moonlight-common-c、FFmpeg 头文件等）
```

## 协议扩展

| 消息 | 用途 |
|---|---|
| `0x5506` | 动态参数（分辨率、码率、HDR 白点） |
| `0x5507` | 分辨率变更通知（主机 → 客户端，触发解码器重建） |
| `0x5508` | 剪贴板同步（文本 / PNG / blob 引用） |

HTTP 侧接口：

| 接口 | 用途 |
|---|---|
| `GET /serverinfo`（47989） | 主机信息、配对状态 |
| `GET /applist`（47984，需客户端证书） | 应用列表与封面 |
| `POST /api/v1/clipboard/blob` | 剪贴板上传图片 |
| `GET /api/v1/clipboard/blob/<id>` | 剪贴板下载图片 |
| `POST /display-scale` | 主机显示缩放调整 |

## 已知限制

- **仅支持 Windows x64**；
- **无手柄输入**（XInput / DInput 尚未实现）；
- **无 mDNS 自动发现**，主机需手动添加；
- **多点触控依赖输入源**：客户端支持多点上报，但触摸设备或转发层只产生单点时，系统侧只会派发一个触点；
- **HDR 与麦克风**尚未支持。

## 路线图

| 功能 | 状态 |
|---|---|
| 手柄支持（XInput / DInput） | 规划中 |
| 多点触控完善 | 规划中 |
| HDR 支持 | 规划中 |
| 麦克风支持 | 规划中 |

## 许可证

本项目遵循 **GNU General Public License v3.0**，详见 [LICENSE](LICENSE)。

内置的 `moonlight-common-c`、FFmpeg、OpenSSL 等组件分别遵循其原有许可证。

## 致谢

- [moonlight-common-c](https://github.com/moonlight-stream/moonlight-common-c) — 会话协议库
- [Sunshine](https://github.com/LizardByte/Sunshine) — 主机端实现
- [moonlight-qt](https://github.com/moonlight-stream/moonlight-qt) — 桌面客户端参考
- [moonlight-vplus](https://github.com/qiin2333/moonlight-vplus) — 协议扩展与触摸实现参考
