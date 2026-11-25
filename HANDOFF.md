# HANDOFF —— 交接说明

> 读者对象：接手的开发者（人或 AI）。这份文件只记**现在仍然有效**的信息：
> 当前状态、代码在哪、为什么这么写、别再踩的坑、接下来做什么。
> 逐日的过程记录见 [PROGRESS.md](PROGRESS.md) 与 `git log`；构建/使用入门见 [README.md](README.md)。

**当前版本：`1.0.50`**（`VERSION` 文件是唯一来源）

---

## 0. 一句话现状

Windows 上能用的 Moonlight 客户端：WinUI 3 界面 + 独立 Win32 串流窗口，复用 `moonlight-common-c`
（AlkaidLab Sunshine 分支），因此支持动态分辨率、剪贴板同步、主机显示缩放等扩展能力。
配对 → 选应用 → 串流 → 控制面板调参 的主链路已经跑通，剩下的都是增强项（§6）。

| 环节 | 状态 |
|---|---|
| WinUI 3 外壳（材质 / 导航 / 原生标题栏 / 卡片） | ✅ |
| 配对（`/pair` 五阶段，PIN） | ✅ |
| 主机列表 / 在线探测 / 应用列表与封面 | ✅ |
| 独立 Win32 串流窗口（标题栏、全屏、比例适配、材质） | ✅ |
| D3D11VA 解码 + NV12 上屏（H.264 / HEVC / AV1） | ✅ |
| Opus → WASAPI 音频 | ✅ |
| 键鼠 / 指针锁定 / 多点触控 | ✅（触控受输入源限制，见 §5） |
| 控制面板（分辨率 / 码率 / 全屏 / 主机缩放） | ✅ |
| 性能浮窗（位置 / 透明度 / 前台置顶） | ✅ |
| 剪贴板（文本 + 图片） | ✅ |
| 主机显示缩放（DPI，`/display-scale`） | ✅ |
| 断线自动重连（3 次后退出并提示） | ✅ |
| 手柄 / mDNS / HDR | ❌ 未做，见 §6 |

---

## 1. 环境与构建

```
Windows 10 1809+ / Windows 11，x64
Visual Studio 2022+（.NET 桌面开发 + 使用 C++ 的桌面开发 + Windows App SDK）
.NET 8 SDK
```

三步构建（**顺序不能反**）：

```bat
cd native
build-mlc.bat        :: 1) moonlight-common-c + enet → native/mlc-build/*.lib
build-native.bat     :: 2) mlrender.dll
build-pairtls.bat    :: 3) pairtls.dll
cd ..
build.bat            :: 4) WinUI 主程序（内部用 -restore + VS 的 MSBuild）
```

硬性约束：**主程序必须用 Visual Studio 自带的 MSBuild**。
`dotnet build` 会报 `MSB4062`（PriGen 任务 `Microsoft.Build.Packaging.Pri.Tasks.dll` 只在 VS 里）。

运行时依赖（**除配对证书外都已入库**）：

| 路径 | 说明 |
|---|---|
| `native/redist/x64/*.dll` | FFmpeg / opus / OpenSSL 运行库 |
| `SDL2.dll` + `SDL3.dll` | sdl2-compat **必须成对**，只放 SDL2 会假死 |
| `native/openssl` | 编译 pairtls / mlrender 用的头与导入库 |
| `native/creds/` | **不入库**：客户端证书 + uniqueid（配对身份） |
| `test-1080p60.mp4` | 可选：`ML_AUTOSTART=demo` 演示模式片源 |

`native/creds/` 缺失时仍能编译运行，但连不上任何主机 —— 需要在应用内对主机完成一次配对生成。

---

## 2. 代码地图

### C#（UI 与胶水层）

| 文件 | 职责 |
|---|---|
| `App.xaml/.cs` | 应用入口、全局异常兜底 |
| `MainWindow.xaml/.cs` | 主窗口：电脑页、主机详情页（应用卡片）、设置页、配对对话框 |
| `StreamWindow.xaml/.cs` | 串流窗口：承载视频、HUD、控制面板；输入分发；剪贴板/分辨率/重连逻辑 |
| `ControlWindow.cs` | 无边框「传输控制」浮层（分辨率/码率/全屏/主机缩放） |
| `VideoSurface.cs` | 与 native 层的 P/Invoke 封装（创建窗口、推包、鼠标键盘、光标） |
| `HostApi.cs` | 主机 HTTP 接口（`/applist`、`/serverinfo`、`/display-scale*`、剪贴板 blob） |
| `HostStore.cs` | `hosts.json` 持久化（按地址/主机名合并） |
| `Pairing.cs` | 配对协议五阶段 |
| `NativeTls.cs` | `pairtls.dll` 封装（带客户端证书的 GET / POST） |
| `StreamSettings.cs` | 设置模型 + `Saved` 事件（材质/行数/码率/剪贴板开关等） |
| `ScreenInfo.cs` | 主屏**物理**分辨率（`EnumDisplaySettings`，与 DPI 缩放无关） |
| `FluentCard.cs` | 卡片磁贴动画（slot 预留悬停放大的空间） |
| `AdaptiveWrapPanel.cs` / `HorizontalGridPanel.cs` | 自适应换行 / 两行横向排列面板 |
| `NativeMouse.cs` / `NativeKeyboardHook.cs` | 指针锁定、全局键盘钩子（Win 键拦截） |
| `WakeOnLan.cs` | 局域网唤醒 |

### native（C++ / Win32）

| 文件 | 职责 |
|---|---|
| `mlrender.cpp` | D3D11 解码 + NV12 上屏 + 子窗口与输入（`ML_HostChildWin32`、`HostWndProc`） |
| `mlsession.cpp` | `moonlight-common-c` 会话封装：`/serverinfo`、`LiStartConnection`、输入/剪贴板/动态参数 |
| `mlaudio.cpp` | Opus 多流解码 → WASAPI |
| `streamwin.cpp` | 串流主窗口：标题栏、全屏、比例适配、材质、控制面板回调 |
| `perfcard.cpp` | 性能浮窗（分层窗口，按前台状态置顶） |
| `hud.cpp` / `panel.cpp` | 旧版 HUD 与原生控制面板 |
| `pairtls.cpp` | OpenSSL 实现的 HTTPS 客户端（配对、剪贴板、显示缩放） |
| `third_party/moonlight-common-c` | MLC 源码（含 Sunshine 扩展），`build-mlc.bat` 编译 |

---

## 3. 关键设计（为什么这么写）

**双窗口结构**：WinUI 3 没有 `HwndHost`，视频用独立顶层 `WS_OVERLAPPEDWINDOW` 承载
（`streamwin.cpp`），WinUI 只做壳与设置。好处是渲染/输入零拷贝、可直接用 Moonlight 原生管线；
代价是两套窗口要同步材质、全屏、输入焦点。

**材质统一**：`StreamSettings.BackdropKind` 是唯一来源。主窗口用 WinUI 的
`MicaBackdrop/DesktopAcrylicBackdrop`；串流窗口是原生窗口，用 `DWMWA_SYSTEMBACKDROP_TYPE (38)`
设同一档；控制面板订阅 `StreamSettings.Saved` 实时跟随。

**控制面板跟随**：串流窗口在 `WM_MOVE/WM_SIZE` 里回调 C#（`SUI_SetMoveCallback`），
C# 立即重算面板位置。早期用 120ms 轮询，拖动时明显滞后。

**性能浮窗层级**：卡片是独立分层窗口，普通层级会被全屏 `WS_POPUP` 下面。
按 `WM_ACTIVATE` 决定 topmost：串流窗口在前台时置顶，切走恢复普通层级（不挡别的应用）。

**全屏**：`ToggleFullscreen` 里**先**设 `g_fullscreen/adjustingAspect`，**再** `SetWindowPos`。
顺序反了的话窗口收到的第一个 `WM_SIZE` 仍走"按比例缩窗"逻辑，结果画面只占屏幕一部分。

**本机分辨率**：用 `ScreenInfo.PrimaryPixels()`（`EnumDisplaySettings` 的 `dmPelsWidth`）。
不能用 `GetSystemMetrics × RasterizationScale`：进程是 per-monitor v2 DPI-aware，
`GetSystemMetrics` 已经是物理像素，再乘缩放会放大 1.5 倍。

**触摸**：`RegisterTouchWindow(TWF_FINETOUCH)` + `WM_TOUCH` → `LiSendTouchEvent`（每指一个 `pointerId`）。
坐标要判别"屏幕坐标 / 窗口坐标"（实测 `TOUCHINPUT` 给的是屏幕坐标），减去 `ClientToScreen` 原点；
主机支持触摸时吞掉触摸模拟出来的鼠标消息（`GetMessageExtraInfo() & 0xFFFFFF00 == 0xFF515700`），
**但只能过滤鼠标消息** —— 把 `WM_TOUCH` 也过滤掉会退化成单点。

**配对身份**：客户端证书 + `creds/uniqueid.txt` 是全局的一份，与主机地址无关。
Sunshine 的 `named_devices` 只记 `cert/uuid/name`，**不绑 IP**。
所以换 IP 不需要重新配对 —— 判据用 **hostname**（`/serverinfo` 明文可取），不要用 `PairStatus`（见 §4）。

**显示缩放**：走 Sunshine 扩展的三个接口（47984，客户端证书）：
`GET /displays`、`GET /display-scale-options`、`POST /display-scale`（JSON `scale_percent`）。
返回的 `supported_scale_percents` 直接填下拉；`restart_required/effective_immediately` 决定提示文案。

---

## 4. 踩过的坑（现象 → 原因 → 修法）

| # | 现象 | 原因 | 修法 |
|---|---|---|---|
| 1 | 构建报 `MSB4062` | `dotnet` SDK 缺 PriGen 任务 | 用 VS 的 `MSBuild.exe` |
| 2 | 首次调用 SDL 卡死 | `SDL2.dll` 是 sdl2-compat，内部要 `SDL3.dll` | 两个 DLL 必须同时存在 |
| 3 | 启动即卡死 | `ApplyAppCardRows()` 在 ItemsPanel 未生成时无限重排 | 只在有应用时重试，最多 3 次 |
| 4 | 启动偶发崩溃（XAML `E_POINTER`） | 设置加载期间设置 `SelectedIndex` 触发了 `SelectionChanged` | 处理函数加 `_settingsReady` 守卫，材质在加载后延迟应用 |
| 5 | 全屏只见上半屏 / 下面露桌面 | `g_fullscreen` 在 `SetWindowPos` 之后才置位 | 先置位再改窗口样式 |
| 6 | 切「本机分辨率」串流卡死 | 控制面板把 `(0,0)` 直接下发，`LiSendDynamicResolution` 返回 -1 | 先解析成真实像素再发 |
| 7 | 性能栏时有时无 | 卡片是普通层级，串流窗口激活后被压到下面 | `WM_ACTIVATE` 控制 topmost |
| 8 | 控制面板「闪一下就消失」 | 全屏下 `SetForegroundWindow` 常失败，失焦看门狗误关 | 同进程前台容忍 + topmost + 1.2s 宽限 |
| 9 | 控制面板拖动时跟不上 | 120ms 轮询 | native `WM_MOVE/WM_SIZE` 回调 |
| 10 | 触控位置漂移 | `TOUCHINPUT` 给的是屏幕坐标，却按窗口尺寸归一化 | 判别后减去 `ClientToScreen` 原点 |
| 11 | 双指只剩单点 | 用了 `TWF_WANTPALM`（要求手掌拒绝） | 改 `TWF_FINETOUCH` |
| 12 | 单击触发两次 | 触摸同时产生 `WM_TOUCH` 和模拟鼠标消息 | 过滤带 `0xFF5157xx` 签名的**鼠标**消息 |
| 13 | 长按出现两个指示标 | 系统触摸反馈（长按圆环）叠在远程光标上 | `SetWindowFeedbackSetting` 关掉全套触摸反馈 |
| 14 | 150% 缩放下本机分辨率变 3840×2400 | `GetSystemMetrics × RasterizationScale` 重复放大 | `ScreenInfo.PrimaryPixels()` |
| 15 | 最外侧应用卡片悬停被裁 | `FluentCard` 的 slot 等于卡片尺寸，放大 5% 溢出 | slot = 卡片 × 1.05，模板宽度用 `SlotWidth` |
| 16 | 主机换 IP 就要求重新配对 | 添加主机的唯一入口就是配对流程 | 先按 hostname 判断是否已有该主机，直接换地址 |
| 17 | `PairStatus` 判断"未配对"但实际能连 | `PairStatus` 按 `uniqueid` 判，与证书是否被接受无关（实测 `/applist` 200、`PairStatus` 0） | 不要用它做判据 |
| 18 | MLC 编译以 `LNK2019` 失败退出 | 自带测试工程 `ControllerHandoffQueueTest.c` 没有 `main` | `build-mlc.bat` 加 `-DBUILD_TESTING=OFF` |
| 19 | 新克隆第一次构建报 `NETSDK1004` | 没有 `project.assets.json` | MSBuild 加 `-restore` |
| 20 | 串流退出后主机仍认为在运行 | 没调 `LiStopConnection` | 退出路径统一 `MS_StopStream` |

---

## 5. 已知问题 / 限制

1. **多点触控取决于输入源**：客户端按 `WM_TOUCH` 上报的触点发送；若触摸设备/转发层只产生单点
   （部分虚拟数字化仪实测 `count=1` 且只有 PRIMARY），画图软件同样只有一条线 —— 是输入源限制。
2. **无手柄支持**：只实现键鼠与触控。
3. **无 mDNS 自动发现**：主机需手动添加；电脑页的「刷新」只做 TCP 端口探测。
4. **HDR / HEVC Main10**：需要 P010 上屏，尚未实现。
5. **换 IP 判定依赖 hostname**：如果主机名也改了，仍会走一次配对（可用 `PairStatus` 兜底，
   但那个字段不可靠，只能当第三备选）。
6. **卡片悬停倍数与 slot 挂钩**：改 `FluentCard.HoverScale` 时必须同步 `AppEntry.SlotWidth/SlotHeight`，
   否则边缘卡片又会被裁。
7. **演示模式**需要自备 `test-1080p60.mp4`（未入库）。

---

## 6. 待办（按建议优先级）

**P0（体验缺口）**
- 手柄输入（SDL3 手柄子系统或 XInput），含热插拔
- 主机「换 IP/换名」后的自动重绑 UI（现在依赖配对对话框里的隐式判定）

**P1（功能对齐）**
- HDR / HEVC Main10（P010 上屏）
- 分辨率热切换的过渡帧（参考 `moonlight-vplus` 的 `ResolutionSwitchController`：请求后等新分辨率首帧，5s 超时再回退重连）
- mDNS 主机发现
- 剪贴板互锁提示（当前是静默同步）

**P2（打磨）**
- 关于页 / 应用图标 / 暗色标题栏一致性
- 性能浮窗更多指标（解码耗时、丢帧率）
- 真实多点触控设备上的回归测试

---

## 7. 发布流程

1. 改代码 → 本地构建验证（`build.bat`，**不要杀正在运行的实例**，用 `-p:OutDir=` 编到临时目录验证）。
2. 递增 `VERSION`（`1.0.NN`），`MoonlightWinUI.csproj` 从该文件读版本。
3. 构建产物覆盖 `bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/`，启动确认。
4. 提交 + 推送：

```bash
git add -A
git commit -m "type: 中文摘要"
git push origin main      # 远端 https://github.com/Apkeniqo/Moonlight-WinUI
```

5. **绝不要提交**：`native/creds/`（证书私钥）、`settings.json`、`*.log`。
   `.gitignore` 里已有兜底规则；新增文件类型时先确认是否含主机信息。

---

## 8. 外部依赖与参考

| 项目 | 用途 |
|---|---|
| [moonlight-common-c](https://github.com/moonlight-stream/moonlight-common-c) | 会话协议（本项目用 AlkaidLab 扩展分支） |
| [Sunshine](https://github.com/LizardByte/Sunshine) | 主机端；扩展接口需要 AlkaidLab 分支 |
| [moonlight-qt](https://github.com/moonlight-stream/moonlight-qt) | 参考实现（输入分发、渲染器结构） |
| [moonlight-vplus](https://github.com/qiin2333/moonlight-vplus) | 扩展协议与触摸/分辨率切换的参考（Android） |

协议扩展速查：`0x5506` 动态参数（分辨率/码率/HDR 白点）、`0x5507` 分辨率通知、
`0x5508` 剪贴板（`kind` 1 文本 / 2 PNG / 3 blob 引用）；
HTTP 侧 `/displays`、`/display-scale-options`、`POST /display-scale`、`/api/v1/clipboard/blob`。

---

## 9. 近期变更（摘要）

| 版本 | 内容 |
|---|---|
| 1.0.50 | 换 IP 判定改用本地 hostname（`PairStatus` 不可靠） |
| 1.0.49 | 换 IP 不再强制重新配对（首版，判据有误） |
| 1.0.48 | 应用卡片悬停放大不再被裁切 |
| 1.0.47 | 本机分辨率不再被 DPI 重复放大 |
| 1.0.46 | 接入主机显示缩放（DPI 三个接口） |
| 1.0.45 | 多点触控、触摸反馈关闭、单双击去重 |
| 1.0.44–1.0.31 | 断线重连、性能浮窗层级、控制面板跟随、刷新按钮等 |
