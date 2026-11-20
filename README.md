<div align="center">

# Moonlight WinUI

**Windows 上的 Sunshine / Moonlight 串流客户端**
WinUI 3 界面 + 原生 Win32 串流窗口

![version](https://img.shields.io/badge/version-1.0.45-blue)
![platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)
![license](https://img.shields.io/badge/license-GPLv3-green)

</div>

---

## 简介

Moonlight WinUI 是一个面向 Windows 的 Moonlight 客户端实现。界面使用 **WinUI 3**
（原生 Mica / Acrylic 材质、系统标题栏、卡片式布局），串流画面则由一个独立的
**原生 Win32 窗口**承载 —— 这样既能拿到 WinUI 3 的现代观感，又保留 Moonlight
原生渲染管线（D3D11 解码 + 纹理上屏）的低延迟与稳定性。

会话层直接复用 [`moonlight-common-c`](https://github.com/moonlight-stream/moonlight-common-c)
（AlkaidLab Sunshine 分支），因此支持该分支的扩展协议：**动态分辨率**、**剪贴板同步**
等主机端增强能力。

服务端需要运行支持扩展的 **Sunshine**（AlkaidLab 分支，`Sunshine v2026.x+`）。
官方原版 Sunshine / NVIDIA GameStream 也可连接，扩展功能会自动降级。

## 特性

### 界面

- **四种窗口材质**：Mica / Mica Alt / Acrylic / 纯色，主窗口、串流窗口、控制面板保持一致
- **系统标题栏** + 内容延伸（`ExtendsContentIntoTitleBar`），窗口边框跟随系统深色主题
- 应用卡片按行数布局（单行 / 双行），尺寸随窗口自适应，间距固定
- 电脑页右上角一键刷新在线状态（TCP 探测，带加载指示）
- 配对流程（PIN + 客户端证书）、应用列表与封面、取消运行中的应用
- Wake-on-LAN 唤醒

### 串流

| 能力 | 说明 |
|---|---|
| 视频 | H.264 / HEVC / AV1，D3D11VA 硬件解码，D3D11 纹理上屏 |
| 音频 | Opus 多流解码 → WASAPI 共享模式播放 |
| 分辨率 | 运行时动态切换（`0x5506`），不改分辨率也可调码率 |
| 画面 | 保持宽高比、全屏黑边用窗口材质填充、窗口/全屏切换 |
| 光标 | 本地光标模式（主机推送形状）与视频内合成两种模式 |
| 剪贴板 | 文本与图片双向同步（`0x5508` + blob 上传/下载） |
| 触控 | 多点触控（Sunshine 扩展协议），坐标按视频区归一化 |
| 性能浮窗 | FPS / 码率 / 网络 / 延迟，位置（6 种）、透明度可调；串流窗口在前台时置顶 |
| 断线重连 | 主机退出或网络中断后自动重连（默认 3 次），失败则结束串流并弹窗提示 |

### 控制面板

串流中唤出的无边框浮层（传输控制）：分辨率、码率、全屏、缩放、退出串流等。

- 实时跟随串流窗口移动 / 缩放（原生窗口消息回调驱动，非轮询）
- 只在"串流窗口失焦"时收起，点画面不会误关
- 打开期间置顶，避免被全屏窗口压住

## 架构

```
MoonlightWinUI (C# / WinUI 3)
├── MainWindow            主窗口：电脑列表、应用列表、设置、配对
├── StreamWindow          串流窗口（承载视频、HUD、控制面板）
├── ControlWindow         无边框控制面板
├── VideoSurface          与 native 层的互操作封装（P/Invoke）
├── HostApi / Pairing     主机 HTTP 接口、配对
├── NativeTls / NativeMouse / NativeKeyboardHook
└── native/
    ├── mlrender.cpp      D3D11 解码、纹理上屏、子窗口与输入
    ├── mlsession.cpp     moonlight-common-c 会话封装、扩展消息
    ├── mlaudio.cpp       Opus 解码 + WASAPI 播放
    ├── streamwin.cpp     Win32 串流窗口（标题栏、全屏、比例适配、材质）
    ├── hud.cpp           性能浮层
    ├── panel.cpp         原生控制面板（旧版路径）
    ├── perfcard.cpp      性能卡片浮窗
    ├── pairtls.cpp       OpenSSL 实现的 HTTPS 请求（配对 / 剪贴板 blob）
    └── third_party/moonlight-common-c   MLC（含 Sunshine 扩展）
```

## 环境要求

- **Windows 10 1809+ / Windows 11**（x64）
- **Visual Studio 2022 或更高版本**，需安装：
  - .NET 桌面开发
  - 使用 C++ 的桌面开发（MSVC + Windows SDK）
  - Windows 应用 SDK C# 模板
- **.NET 8 SDK**
- 无需 MSIX 打包：`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`，
  生成的 exe 可直接运行，不依赖机器上预装的 Windows App SDK 运行时。

## 构建

### 1. 编译原生 DLL

```bat
cd native
build-mlc.bat         :: 1) 编译 moonlight-common-c + enet → native/mlc-build
build-native.bat      :: 2) mlrender.dll
build-pairtls.bat     :: 3) pairtls.dll（OpenSSL HTTPS 客户端）
```

- 顺序不能反：`build-native.bat` 会链接第一步产出的 `moonlight-common-c.lib` / `enet.lib`；
- `build-mlc.bat` 只编译库（`-DBUILD_TESTING=OFF`），不编译 MLC 自带的测试工程；
- `build-mlc.bat` 需要 **CMake**（VS 安装目录自带一份，脚本会自动查找）；
- FFmpeg / opus 的头文件与导入库随仓库提供（`native/third_party/libs/windows`）；
- OpenSSL 的头文件与导入库也随仓库提供（`native/openssl`）；
- 脚本会自动定位 VS 的 `vcvars64.bat`，产物输出到 `native/` 下。

### 2. 编译主程序

> ⚠️ **必须使用 Visual Studio 自带的 MSBuild，不能用 `dotnet build`。**
> `dotnet` SDK 缺少 PriGen 任务（`Microsoft.Build.Packaging.Pri.Tasks.dll`），会报 `MSB4062`。

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
    -restore MoonlightWinUI.csproj /p:Configuration=Debug /p:Platform=x64

.\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\MoonlightWinUI.exe
```

也可以直接运行根目录的 `build.bat`（内部即为上述命令，路径按本机 VS 安装位置调整）。

## 运行时依赖

**除配对证书外，运行时依赖已全部随仓库提供** —— clone 后按上面的步骤编译即可直接启动使用。

| 路径 | 内容 | 状态 |
|---|---|---|
| `native/redist/x64/` | `avcodec-63.dll` `avformat-63.dll` `avutil-61.dll` `swscale-10.dll` `dav1d.dll` `opus.dll` `libssl-3-x64.dll` `libcrypto-3-x64.dll` | ✅ 已内置 |
| `SDL2.dll` + `SDL3.dll` | 根目录 | ✅ 已内置（sdl2-compat **必须与 SDL3.dll 成对存在**，只放 SDL2 会在首次调用时卡死） |
| `native/third_party/libs/windows/` | FFmpeg / opus 头文件与导入库 | ✅ 已内置（编译用） |
| `native/openssl/` | OpenSSL 头文件与导入库 | ✅ 已内置（编译用） |
| `native/creds/` | `client.crt` `client.key` `uniqueid.txt` | ⚠️ **需自备** |
| `test-1080p60.mp4` | 演示模式（`ML_AUTOSTART=demo`）片源 | ⚠️ 可选，未入库 |

`native/creds/` 是客户端的配对身份（证书 + 私钥），出于安全考虑不入库。可以：

- 从已有的 Moonlight Qt 版安装目录复制一份；
- 或者在应用内对主机重新配对一次，自动生成。

缺少它时程序仍可编译运行，只是不能直接连接已配对过的主机。

## 使用

1. 启动应用 → 「配对电脑」→ 输入主机 IP（或从已配对列表中选择）
2. 主机侧输入 PIN 完成配对
3. 在电脑页刷新在线状态 → 选择主机 → 选择应用 → 开始串流
4. 串流中点击画面左上角的 ☰ 唤出控制面板：切换分辨率、码率、全屏，或退出串流

### 环境变量

| 变量 | 作用 |
|---|---|
| `ML_AUTOSTART=<host>` | 启动后直接连接指定主机并开始串流 |
| `ML_AUTOSTART=demo` | 演示模式：播放本地测试片源，不连接主机 |
| `ML_SHOWCONTROL=1` | 演示模式下自动打开控制面板（调试用） |

## 已知限制

- **仅 Windows x64**：WinUI 3 与原生层的耦合依赖 Windows API
- **无手柄支持**：目前只实现键鼠与触控输入
- **无 mDNS 自动发现**：主机需手动添加；电脑页的「刷新」只做 TCP 在线探测
- **多点触控取决于输入源**：客户端支持多触点上报，但若触摸设备/转发层只产生单点（例如某些虚拟数字化仪），系统侧只会派发一个触点
- **主机 DPI 缩放**：暂不支持通过客户端调整主机的 Windows 显示缩放（Sunshine 当前版本未对外开放该接口）

## 协议扩展

客户端实现了 AlkaidLab Sunshine 分支的两个扩展：

| 消息 | 用途 |
|---|---|
| `0x5506` | 动态参数（分辨率、码率、HDR 白点） |
| `0x5507` | 分辨率变更通知（主机 → 客户端，触发解码器重建） |
| `0x5508` | 剪贴板同步（`kind`：1 文本 / 2 PNG / 3 blob 引用） |

HTTP 侧：

- `GET /serverinfo`（47989）：主机信息、配对状态
- `GET /applist`（47984，需客户端证书）：应用列表与封面
- `POST /api/v1/clipboard/blob`：图片剪贴板上传（`X-Clipboard-Mime` 头）
- `GET /api/v1/clipboard/blob/<id>`：图片剪贴板下载

## 相关项目

- [moonlight-common-c](https://github.com/moonlight-stream/moonlight-common-c) — 会话协议库
- [Sunshine](https://github.com/LizardByte/Sunshine) — 主机端（本项目主要搭配 AlkaidLab 分支）
- [moonlight-qt](https://github.com/moonlight-stream/moonlight-qt) — 桌面客户端参考实现
- [moonlight-vplus](https://github.com/qiin2333/moonlight-vplus) — 协议扩展与触摸实现参考

## 许可证

本项目沿用 [moonlight-qt](https://github.com/moonlight-stream/moonlight-qt) 的许可证：
**GNU General Public License v3.0**，详见 [LICENSE](LICENSE)。

> 内置的 `moonlight-common-c`、Qt 相关代码与字体资源同样遵循 GPLv3。
