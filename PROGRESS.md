# 进度总览（2026-10-23）

> **一句话**：WinUI 3 客户端已经能连上真实 Sunshine 主机 —— HEVC 1080p60 画面、键盘/鼠标/滚轮、
> 指针锁定、48kHz 立体声音频全部跑通，窗口关闭能干净收尾。剩下的是**手柄、多声道音频、
> HDR/Main10、配对 UI** 这些"从能用到好用"的部分。
>
> 深度细节在 `HANDOFF.md`（实现决策 + 踩坑记录）；本文是"现在到哪了、下一步干什么"的地图。
>
> **双机**：笔记本 `D:\Projects\Moonlight WinUI3` ↔ 桌面机 `E:\Projects\Moonlight WinUI3`，内容随时
> 互相覆盖同步；两份都能直接 `build.bat` / 跑 exe，**没有需要跟着机器改的路径**（见 §6、§7）。

---

## 1. 现在能做什么（验收脚本）

```powershell
# 1) 跑起来（默认连 DESKTOP-M9RGVD3）
.\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\MoonlightWinUI.exe
```

1. 启动后是**电脑页**：左边偏上一排横向电脑卡片（点卡片进详情页），右上角是「配对电脑」；
   页面没有别的按钮/提示，窗口材质在设置页。**卡片来自 `hosts.json`**（不是写死的），
   在线状态是真探测（TCP 连该主机配置的 HTTP 端口，默认 47989）；配对成功会自动加一张卡。
2. **配对**：点右上角「配对电脑」→ 弹出模态窗（主界面此时不能操作），填 **IP / 主机名** 和 **端口**
   （默认 47989，HTTPS 自动取 -5）→ 点「连接」→ 弹窗里会显示 4 位 PIN →
   去主机网页 `https://<IP>:47990` 输入并确认；成功后弹窗自动关闭。取消则什么都不做。
3. **点电脑卡片** → 进入该主机的详情页：实时读 `/applist`，应用以**单行横向卡片**铺开、整条垂直居中
   （封面来自 `/appasset?asset=boxart`，主机没给就用首字占位图；滚轮一格横移一张，两侧按露出比例渐隐）。
   点卡片就按它的 appid 开串流；读不到会说明原因（未配对 / 连不上 / 主机返回码）。
4. **设置**（左侧 Settings）：窗口材质（Mica / Mica Alt / Acrylic / Solid）、分辨率（720p–4K）、
   刷新率（30–144 Hz）、码率（5–100 Mbps）、编码（自动 / H.264 / HEVC / AV1）、窗口模式（窗口 / 全屏）、
   连接后自动锁定指针、交换鼠标左右键、反转滚轮方向、显示性能信息、串流时保持屏幕常亮、
   串流结束后退出应用 —— 后六项与 Qt 版（moonlight-qt）同名同默认值。改完立即写入 `settings.json`；
   「应用并重新连接」会按新参数重开串流窗口。
5. 点某个应用 → **新开一个独立串流窗口**（自绘标题栏，材质跟主界面一样是 Mica），3~5 秒后出画面。
   画面右上角有**悬浮齿轮按钮**，点开「传输控制」浮层：分辨率、码率、全屏开关，
   点「应用并重连」立刻按新参数重连（不用回主窗口）。
6. 串流窗口里的鼠标默认走**绝对模式**：本地指针原地不动，主机光标按指针在画面里的位置直接映射
   （按流分辨率缩放、不受主机指针加速影响），所以不会有"点一下跳回中间"或位置差。
   要 raw 相对增量的游戏把设置里的「绝对鼠标」关掉，此时点画面会进入相对捕获
   （藏光标 + 夹在画面里 + 钉中心），**Ctrl+Alt+Shift+Q** 释放；切走窗口、关窗也会释放。
7. 串流画面**顶部居中**有一条性能浮窗（一行）：`60 fps · 编解码 7.0 / 0.8 ms · 网络 0.5 ms ·
   丢包 0.0% · 22.8 Mbps`（fps / 主机编码+本地解码 / RTT÷2 / 视频帧丢失率 / 实时码率），
   每 0.5 s 刷新；设置里「显示性能信息」关掉就不显示，退回底部那行包数/音频详细统计。
8. 关掉串流窗口 → `LiStopConnection` 干净收尾，主机会话释放；主窗口还在，可以换个主机再开一个。

> 同一时间只允许一个串流窗口（native 的 `ML_*` 是进程级单例）：再点应用会先关旧的再开新的。

无头验证（不用手点）：

```powershell
$env:ML_AUTOSTART="DESKTOP-M9RGVD3"   # 启动即开串流窗口（=demo 则演示流）
$env:ML_SELFTEST="1"                  # 连上后自动"顶光标→移动→右键→ESC"，结果写 input-selftest.log
$env:ML_PAIR="LAPTOP-8BQF1SDN"        # 启动即配对，结束自动退出；PIN 见 pairing.log 或 ML_PAIR_PIN 指定
$env:ML_TLS_TEST="LAPTOP-8BQF1SDN"    # 只验客户端证书 + HTTPS，结果写 pairing.log
```

---

## 2. 完成情况

| 模块 | 状态 | 证据 |
|---|---|---|
| WinUI 3 外壳（Mica / NavigationView / 原生标题栏） | ✅ | `shot-final.png` |
| 独立串流窗口（连接时新开） | ✅ | `StreamWindow`；演示流/真串流都走它，关窗即断流（`shot-split-windows.png`） |
| 电脑页（卡片自适应换行） | ✅ | `AdaptiveWrapPanel`：列数/列宽随窗口变，窄了自动换行不裁切；卡片来自 `hosts.json`、在线状态真探测 |
| 应用卡片布局（两行横滚） | ✅ | `HorizontalGridPanel` Rows=2，整页垂直居中，滚轮横移一列；间距 32→20 |
| 磁贴动画（电脑/应用卡片） | ✅ | 固定格子（卡片×1.05）+ 格子内整块缩放 + 独立命中层：悬停 105%、径向高光跟鼠标、按下 98%，命中区域恒定（不抖、可点）；`FluentCard.cs` |
| 配对弹窗（IP / 端口，模态） | ✅ | `ContentDialog`，配对期间主界面不可操作；`shot-pair-dialog.png` |
| 设备卡片 → 主机详情页（`/applist`） | ✅ | 点卡片读应用列表，点应用按 appid 连接；`shot-host-apps.png`、`HostApi.cs` |
| 应用封面卡片（`/appasset`） | ✅ | 读主机 boxart（JPEG/PNG），缺图显示首字占位；`shot-app-cards.png` |
| 单行横向卡片条（滚轮横滚 / 两侧渐隐） | ✅ | 170×240 封面、32 间距、居中；`shot-app-strip.png` |
| 设置页（对接 Qt 版设置项） | ✅ | 材质/分辨率/刷新率/码率/编码/窗口模式/指针锁定/左右键互换/滚轮反转/性能叠加/保持常亮/结束退出；`shot-settings.png` |
| 编码选择（H.264 / HEVC / AV1） | ✅ / ⚠️ | 自动、H.264、HEVC 实测协商成功（`format=0x1/0x100`）；AV1 客户端已实现（软解），但本机主机只报 `0x301`（RX 5700 XT 无 AV1 编码器），选中会提示"主机不支持" |
| D3D11 交换链挂 SwapChainPanel | ✅ | — |
| d3d11va 硬解 + NV12 上屏 + 缩放 | ✅ | 解码+上屏 0.27ms，165fps |
| 网络包接口 + 帧节奏 | ✅ | `ML_PushPacket` / `ML_Frame` |
| moonlight-common-c 编译 | ✅ | `native/mlc-build/*.lib`（含 enet.lib） |
| 客户端配对身份 | ✅ | `native/creds/`（uniqueid 5325b8a3fdd68731）；已配对 DESKTOP-M9RGVD3 + LAPTOP-8BQF1SDN |
| 配对流程（`/pair` 五阶段） | ✅ | `Pairing.cs` + `pairtls.dll`；LAPTOP 实测 `PairStatus=1`（`pairing.log`） |
| `/serverinfo` + `/launch` + `/resume` + `/applist` | ✅ | 见 §4 实测 |
| RTSP 握手（`rtspenc://`，含控制流加密 v2） | ✅ | `decoder.log` 阶段日志 |
| H.264 / HEVC(Main) 切换 | ✅ | `format=0x100`，`shot-live-stream.png` |
| 键盘 / 鼠标移动 / 按键 / 滚轮 | ✅ | `shot-input-menu.png`、`shot-ui-keys.png` |
| 音频 Opus 多流 → WASAPI 共享模式 | ✅ | 23.2s 播 1,118,496 帧，0 解码错误 |
| 控制台探针（不依赖 UI） | ✅ | `native/msprobe.exe` |
| 演示流（本地文件喂包） | ✅ | `ML_StartFile` / `ML_StopFile` |
| 音频多声道 / 设备选择 | ❌ | 只做立体声 + 默认设备 |
| 指针锁定（relative capture） | ✅ | 藏指针 + ClipCursor + 边缘回中；`shot-pointerlock.png`、`pointer-lock.log`（`ML_LOCKTEST=1` 自测） |
| 鼠标同步（绝对模式，不捕获） | ✅ | `ML_StreamSize` 缩放 + 指针位置直接映射；绝对模式**不进指针锁定**，本地指针不动、主机光标跟指针；相对模式仍可（设置里关「绝对鼠标」） |
| 性能浮窗（可调透明度/位置） | ✅ | fps / **编解码方式** / 编解码耗时 / 网络 / 丢包 / 码率；设置里可调透明度与六个位置 |
| 串流窗口控制（可拖动齿轮浮层） | ✅ | 按钮可拖动；码率走 `/bitrate` **即时生效不重连**（200 实测）；分辨率因主机要求控制流协议，暂回退重连 |
| 手柄 / 触摸 / 剪贴板 / 麦克风 | ❌ | — |
| HDR / HEVC Main10 | ❌ | 只做 8-bit NV12 |

---

## 3. 代码地图

| 文件 | 行数 | 职责 | 关键导出 |
|---|---|---|---|
| `native/mlrender.cpp` | 809 | D3D11 渲染、FFmpeg d3d11va 解码（H264/HEVC）+ AV1 软解上屏、包队列 | `ML_Init` `ML_PushPacket` `ML_Frame` `ML_SetVideoFormat` `ML_StopFile` `ML_Resize` `ML_Close` |
| `native/mlsession.cpp` | 1384 | OpenSSL HTTPS、XML 解析、launch/resume、`LiStartConnection`、输入转发、编码偏好 | `MS_Init` `MS_Status` `MS_SetVideoCodec` `MS_Start` `MS_Stop` `MS_SendKey` `MS_SendMouseButton` `MS_SendMouseScroll` `MS_SendMouseHScroll` `MS_SendMouseMove` `MS_Stats` |
| `native/mlaudio.cpp` | 557 | Opus 多流解码 + WASAPI 播放 | `MA_Init` `MA_Start` `MA_Stop` `MA_Shutdown` `MA_Push` `MA_Stats` |
| `native/pairtls.cpp` | 285 | 配对收尾用的 HTTPS GET（OpenSSL + 客户端证书，不碰证书库） | `pairtls.dll!PT_Get` |
| `Pairing.cs` | — | `/pair` 五阶段：SHA-256 派生 AES、证书签名、MITM/PIN 校验 | `Pairing.PairAsync` `ProbeTlsAsync` |
| `native/msprobe.cpp` | 137 | 控制台探针：init→start→轮询状态→stop | — |
| `VideoSurface.cs` | 387 | C# 包装：D3D11 设备/交换链/面板、所有 P/Invoke、输入 API | `StartStream` `StopStream` `StreamStatus` `AudioStats` `SendKey` |
| `MainWindow.xaml(.cs)` | 199 / 1012 | 启动器：电脑卡片（读 `hosts.json`）、主机详情页、配对弹窗、设置页 | — |
| `StreamWindow.xaml(.cs)` | 47 / 773 | 独立串流窗口：视频面、键鼠输入、指针锁定、性能浮窗 | — |
| `StreamSettings.cs` | 117 | 串流参数 + `settings.json` 读写（含 Qt 同名项、编码偏好） | `StreamSettings.Load/Save` |
| `HostStore.cs` | 136 | 电脑列表 `hosts.json` 读写（地址/显示名/端口，按地址去重） | `HostStore.Load/Save/AddOrUpdate` |
| `HostApi.cs` | 136 | `/applist` 读取与解析（`AppEntry`）+ `/appasset` 封面下载 | `HostApi.GetAppList` `GetBoxArt` |
| `NativeTls.cs` | 91 | `pairtls.dll` 的 P/Invoke 封装（文本 / 二进制两种） | `NativeTls.TryGet` `TryGetBytes` |
| `native/harness.cpp` | 91 | 早期 FFmpeg 解码探针（已不是主路径） | — |

构建脚本：根目录 `build.bat`（→ WinUI exe，vswhere 找 MSBuild）；native 目录下
`build-native.bat`（→ mlrender.dll）、`build-msprobe.bat`（→ msprobe.exe）、
`build-mlc.bat`（→ MLC 静态库，仅改动 MLC 时需要）。三个 native 脚本都先调 `native/env.bat`
自动定位依赖与 VS；运行期 DLL 在 `native/redist/x64/`，**全仓没有写死的机器路径**。

---

## 4. 数据流

```
启动器（MainWindow，无画面）
  点「演示流 / 连接桌面」→ new StreamWindow(host, demo) → 独立窗口，持有唯一的 VideoSurface

串流窗口（StreamWindow）UI 线程
  CompositionTarget.Rendering → VideoSurface.Render() → ML_Frame()
     → 取包队列 → avcodec(d3d11va, H264/HEVC) → NV12 双平面 SRV → swapchain

会话线程（mlsession 自己起）
  MS_Start → /serverinfo → 生成 rikey → /launch 或 /resume → LiStartConnection

MLC 视频线程 → DrSubmitDecodeUnit → 拼 LENTRY 链 → ML_PushPacket（入队）
MLC 音频线程 → ArDecodeAndPlaySample → MA_Push → opus 解码 → 环形缓冲
                                              → 音频线程 → IAudioRenderClient

输入
  WinUI Pointer/Key 事件 → VideoSurface.SendXxx → LiSendXxx → 控制流（ENet）
```

---

## 5. 实测数字（主机 DESKTOP-M9RGVD3 / 10.112.26.51）

- Sunshine `v2026.1020.23431.杂鱼`，`ServerCodecModeSupport=0x301`（H264 + HEVC + HEVC10）
- `MS_Start` → 出画面 **3~5 秒**；二次连接走 `/resume`，约 **1.5 秒**拿到 RTSP URL
- 视频：`format=0x100`（HEVC）1920×1080@60，请求 20 Mbps，实际约 60 包/秒
- 解码 + 上屏 **0.27 ms**，UI 165 fps（测试窗口尺寸下）
- 音频：48 kHz / 2ch，WASAPI 缓冲 **1056 帧（22 ms）**，欠载 1 次（启动）、丢 0、解码错误 0
- 输入：右键菜单/ESC、Shift+F10 都在主机上生效
- 指针锁定（本机 125% DPI 自测）：935×318 DIP 的面板在屏幕上占 1169×398 px（×1.25），
  钉中心后 `GetCursorPos` 正好落在矩形中心、`ShowCursor` 计数 -1 → 0，退出后指针回到原位置
- 配对（LAPTOP-8BQF1SDN / 10.112.32.221）：阶段 1 等网页确认约 8 s，阶段 2→5 合计 **62 ms**，
  五阶段全绿，收尾后 `PairStatus=1`（`pairing.log`，2026-10-23 02:08）

---

## 6. 环境与凭据（换机器必读）

- **主机**：`DESKTOP-M9RGVD3`（10.112.26.51），Sunshine 在 47984（HTTPS）/ 47989（HTTP）/ 48010（RTSP）。
  流媒体端口实测：视频 47998、音频 48000、控制 47999。
- **证书**：`native/creds/client.crt|client.key`（PEM）+ `uniqueid.txt`（`5325b8a3fdd68731`）。
  这份身份已配对**两台主机**：`DESKTOP-M9RGVD3`（10.112.26.51）和 `LAPTOP-8BQF1SDN`（10.112.32.221），
  两边 HTTPS `/serverinfo` 都是 `PairStatus=1`。换新主机先在应用里配对（§1 第 2 步）。
- **两份拷贝**：笔记本 `D:\Projects\Moonlight WinUI3` ↔ 桌面机 `E:\Projects\Moonlight WinUI3`，
  内容一致、随时互相覆盖同步。**两边都能直接构建**：路径差异由 `native/env.bat`（依赖/VS 探测）
  和 `native/redist/x64/`（运行期 DLL 随仓库走）吸收，同步完不需要改任何文件。
  同步时 `bin/` 可以带（exe 直接能跑），`obj/` 建议排除（里面是本机的 NuGet/MSBuild 缓存路径）。
- **依赖的 Qt 仓库**（FFmpeg 头/库、MLC 源码、opus）：放在项目**同级**
  `Moonlight-Qt-with-FluentUI`（笔记本 `D:\Projects\...`、桌面机 `E:\Projects\...` 都认）；
  放在别处就 `set ML_DEPS=<仓库根>`，只需设一次/一台。
- **低完整性（这台机器特有）**：`E:\Projects` 整棵树带 `Mandatory Label\Low Mandatory Level`，
  从 `bin` 启动的 exe 就是 Low IL，写不了 `%APPDATA%`（用户证书库也在那里）。
  配对收尾因此走 `pairtls.dll`（OpenSSL，直接读 PEM）；想在桌面上恢复普通权限，用管理员执行
  `icacls "E:\Projects" /setintegritylevel Medium /T`。笔记本那份没有这个标签，SChannel 回退路径也能用。

---

## 7. 在新机器上继续

不需要改任何路径。首次在本机构建时按下面走：

```powershell
# C# 侧（两台机器通用；build.bat 用 vswhere 找 MSBuild，不写死 VS 路径）
.\build.bat                # = MSBuild MoonlightWinUI.csproj /p:Configuration=Debug /p:Platform=x64

# 原生 DLL（只改了 native\*.cpp 时才需要；要求项目同级有 Moonlight-Qt 仓库）
cd native
.\build-native.bat        # mlrender.dll（含 mlsession/mlaudio）
.\build-msprobe.bat       # msprobe.exe
.\msprobe.exe DESKTOP-M9RGVD3 1 40    # 先验证网络/握手，不牵扯 UI

# 只有第一次在这台机器上（或 MLC 源码变了）才需要；换过依赖目录要先删 mlc-build
.\build-mlc.bat
```

> 别用 `dotnet build`：WinUI 的 PRI 生成任务在 VS 的 MSBuild 扩展目录里，纯 .NET SDK 会报
> `MSB4062 ... Microsoft.Build.Packaging.Pri.Tasks.dll`。`build.bat` 就是为绕开这个。

**唯一可能要设的环境变量**：Moonlight-Qt 仓库既不在项目同级、也不在 `D:\Projects` / `E:\Projects` 下时，
`set ML_DEPS=<仓库根>`（`env.bat` 会把最终用的路径和 vcvars 打出来，便于确认）。

**只跑不编译**：`bin\x64\Debug\...\win-x64\MoonlightWinUI.exe` 自带运行时，双击即可。

---

## 8. 已知限制 / 技术债（按优先级）

**高（影响"能不能当客户端用"）**

1. ~~**没有指针锁定**~~ ✅ 已做（2026-10-23，`MainWindow.xaml.cs` + `NativeMouse.cs`）：藏指针、
   `ClipCursor` 夹在画面里、贴近边缘钉回中心，`Ctrl+Alt+Shift+Q` 释放。
2. **音频设备不可选**：固定走系统默认输出；没有应用内音量/静音。
3. **手柄 / 触摸没接**：MLC 的 `LiSendControllerEvent` 系列一条没调。

**中**

4. 音频只协商立体声（5.1/7.1 需要按 `mapping` 重排 + 正确声道掩码）。
5. HDR / HEVC Main10 没做（要 P010 上屏 + HDR 元数据，`sc.hdrMode`、`colorSpace` 也要调）。
6. ~~配对流程没做~~ ✅ 已做：`Pairing.cs` 五阶段 + `pairtls.dll` 收尾，LAPTOP 已实测（§2/§5）。
7. 码率/分辨率固定（1080p60 / 20Mbps），没有 ABR、没有用户在 UI 里选。

**低**

8. 剪贴板同步、文件传输、麦克风、遥控器/输入法。
9. 光标形状没接（MLC 有 `cursorUpdate` 回调）。
10. 断线不重连：`connectionTerminated` 只改状态，不自动 resume。
11. 暂停/恢复演示流时解码器复用可能有一帧花屏（切流时清过队列，但没 flush 解码器）。

---

## 9. 下一步建议（具体到改哪里）

1. ~~**指针锁定**~~ ✅ 已完成：`TryEnterPointerLock / ExitPointerLock / HandleLockedPointerMove`
   （`MainWindow.xaml.cs`）+ Win32 封装（`NativeMouse.cs`）。细节见 `HANDOFF.md` §2.8。
2. **手柄**（半天）：后台线程轮询 XInput → `LiSendControllerEvent`；注意 `LiSendMultiControllerEvent`
   的 `activeGamepadMask`，以及 `/launch` 的 `remoteControllersBitmap` 目前写死 0。
3. **音频设备选择/音量**（半天）：`IAudioSessionManager2` 枚举会话 + `ISimpleAudioVolume`；
   或者用 `IMMDeviceEnumerator::EnumAudioEndpoints` 在 UI 里选设备（`mlaudio.cpp` 里 `CreateDevice`
   已经按名字记日志，加个 deviceId 参数即可）。
4. **5.1/7.1**（1 天）：`sc.audioConfiguration = AUDIO_CONFIGURATION_51_SURROUND`，`mlaudio.cpp`
   按 `channelCount` 设 `dwChannelMask`（`KSAUDIO_SPEAKER_5POINT1`），并确认 opus mapping 与
   Windows 声道顺序一致。
5. **HDR/Main10**（2~3 天）：`sc.hdrMode=1` + `supportedVideoFormats |= VIDEO_FORMAT_H265_MAIN10`，
   `mlrender` 的 `DrawFrame` 支持 `AV_PIX_FMT_P010`（D3D11 纹理格式 `DXGI_FORMAT_P010`），
   PS 换成 PQ/HLG 色调映射；`ML_SetVideoFormat` 里区分 8/10-bit。

---

## 10. 调试入口

| 现象 | 先看哪里 |
|---|---|
| 连不上 / 报错 | `decoder.log` 里 `[session]` 行；`msprobe.exe` 打一轮 |
| `401 Certificate verification failed` | 这台主机还没配对这个客户端：点「配对」走一次 `/pair`（§1 第 2 步） |
| `launch 返回 404 App not found` | appid 不对（会自动查 `/applist`，看日志确认） |
| `400 An app is already running` | 主机会话没释放（会自`/resume`，或重启 Sunshine） |
| 黑屏但有包 | `ML_Init` 失败（D3D 设备/解码器），看 `[session]` 和 mlrender 日志 |
| 没声音 | `[audio]` 行：设备名、`Initialize` 的 HRESULT、欠载/解码错误 |
| 按键没反应 | 画面有没有焦点（点一下）；`MS_SendKey` 返回 -1 表示没连上 |
| 指针锁不住 / 光标一直显示 / 退出后光标不回来 | `pointer-lock.log`（enter/exit 的屏幕矩形、`hideCount`/`showCount`）；兜底：Ctrl+Alt+Shift+Q、切走窗口都会解锁 |
| 配对失败 / 提示 PIN 不对 | exe 目录 `pairing.log`（每阶段一行）；主机网页 `https://<主机>:47990` 的 PIN 页要输对 4 位数字 |
| 配对卡在阶段 1 | 等你在主机网页确认；超过 5 分钟会超时，重来一次即可 |
| 设置不生效 / 分辨率没变 | 看 exe 目录 `settings.json` 有没有写进去；`decoder.log` 里“视频协商”那行是主机实际给的值 |
| 主机详情页没有应用 | 页面上的提示就是原因：401=没配对、连不上=主机关着或端口不通；`/applist` 只在 47984 上 |
| 应用卡片没有封面 | 主机 `/appasset` 没返回 PNG/JPEG 时显示首字占位（Sunshine 一般会回自己的默认图）；解码失败也只退回占位 |
| 想单独验证指针锁定 | `$env:ML_LOCKTEST="1"` 再启动 exe：演示流下 2 秒后自动进锁、2 秒后释放 |

`decoder.log` 有三类前缀：`[session]`（网络/握手）、`[audio]`（音频）、无前缀（mlrender 解码）。
日志写在 **exe 所在目录**。

---

## 11. 关键参数（要调先来这里）

| 参数 | 位置 | 当前值 |
|---|---|---|
| 分辨率 / 帧率 / 码率 | 设置页 → `settings.json`（`StreamSettings`） | 默认 1080p60 / 20 Mbps；可选 720p–4K、30–144 Hz、5–100 Mbps |
| appid | 详情页 → 点应用卡片 | 用主机 `/applist` 里的真实 ID；不再是写死 1 |
| 性能浮窗 | `MS_Perf()`（native）+ 串流窗口 0.5 s 定时器 | fps/解码 ← `ML_Stats`；编码 ← `DECODE_UNIT.frameHostProcessingLatency`；网络 ← `LiGetEstimatedRttInfo`÷2；丢包 ← `LiGetEstimatedVideoFrameLossPercentage`；码率 ← `LiGetRTPVideoBytesReceived` 差分 |
| 视频编码 | 设置页「编码」→ `MS_SetVideoCodec`（0 自动 / 1 H264 / 2 HEVC / 3 AV1） | 默认自动 = 报 H264 + HEVC + AV1（主机支持才算）；AV1 走软解 |
| 音频配置 | `sc.audioConfiguration` | `AUDIO_CONFIGURATION_STEREO`（0x302CA） |
| 包大小 / 加密 | `sc.packetSize` / `sc.encryptionFlags` | 1024 / `ENCFLG_ALL` |
| 环形缓冲 | `mlaudio.cpp` `kRingMs` | 250 ms |
| WASAPI 缓冲 | `mlaudio.cpp` `kWasmBufferHns` | 200000（20 ms，实测 22 ms） |
| HTTPS / launch 超时 | `mlsession.cpp` `kServerInfoTimeoutMs` / `kLaunchTimeoutMs` | 8 s / 120 s |
| 指针边缘回中 | `MainWindow.xaml.cs` `HandleLockedPointerMove` | 边距 `min(60px, 短边×20%)` |
| 指针释放快捷键 | `MainWindow.xaml.cs` `VideoPanel_KeyDown` | `Ctrl+Alt+Shift+Q` |
| 指针锁定日志 | exe 目录 `pointer-lock.log` | 每次 enter/exit 一行；绝对模式前 8 次 `abs x,y / WxH rc=… d=(dx,dy)` |
| 配对端口 | 配对弹窗的「端口」（`Pairing.PairAsync` 的 `httpPort`） | 默认 HTTP 47989（HTTPS 自动取 -5），可在弹窗里改 |
| 配对 PIN / 超时 | 弹窗自动生成 / `ML_PAIR_PIN` | 4 位随机；阶段 1 最多等 5 分钟 |
| 配对日志 | exe 目录 `pairing.log` | 每阶段一行 + 结果 |
| 电脑列表 | exe 目录 `hosts.json` | 地址 + 显示名 + HTTP 端口；配对成功自动追加，按地址去重 |
| 在线探测 | `MainWindow.ProbeHostAsync` | TCP 连主机的 HTTP 端口，1.2 s 超时 |
| 鼠标模式 / 缩放 | 设置页「绝对鼠标」→ `MS_SendMousePosition`; `ML_StreamSize` | 默认绝对；缩放 = 流分辨率 ÷ 面板物理尺寸；单次 >250px 视为瞬移丢弃 |
