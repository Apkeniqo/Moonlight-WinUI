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

1. 改代码 → 本地构建验证：**先结束正在运行的 `MoonlightWinUI.exe`**（否则 `mlrender.dll` / 主程序被占用无法覆盖），
   直接编到默认输出目录，**不要**用 `-p:OutDir=` 编到临时目录。
2. 递增 `VERSION`（`1.0.NN`），`MoonlightWinUI.csproj` 从该文件读版本。
3. 构建产物直接落在 `bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/`，从该目录启动确认。
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

### 1.0.51 —— 游戏鼠标支持（相对捕获）

- **现象**：进入 3D 游戏后光标不隐藏、视角不随鼠标转动。
- **原因**：`TryEnterPointerLock()` 在 `AbsoluteMouse == true`（默认）时直接 `return false`，
  永不进入相对捕获；而 `SendPointerDeltaHost()` 在绝对模式下只发 `LiSendMousePositionEvent`，
  游戏需要的相对增量（`LiSendMouseMoveEvent`）从未发过。
- **修法**（`StreamWindow.xaml.cs`）：
  - 取消"绝对模式不允许锁定"，点画面即可锁定（游戏需要时）；
  - `SendPointerDeltaHost()` 增加**锁定优先**分支：`_pointerLocked` 时走 `SendMouseMove(dx, dy)`，未锁定才按 `AbsoluteMouse` 走绝对；
  - `ApplyRemoteCursorMode()` 的 `wantLocal` 增加 `_pointerLocked`，锁定期间要求主机别画光标，本地也隐藏；
  - 进入/退出锁定时把 `_cursorModeApplied` 置 false 并重新应用，切换即时生效。
- 退出锁定（Esc / Ctrl+Alt+Shift+Q）恢复绝对模式与主机光标。

### 1.0.52 —— 游戏鼠标：Raw Input + 原生置空光标

- **现象（1.0.51 后）**：进入游戏视角只有"轻微变化"，光标仍显示。
- **原因**：锁定后的增量仍来自 WinUI `PointerMoved` + 系统光标位置差 —— 指针被 `SetCursorPos` 归中后
  位置不再变化，事件稀疏且增量被窗口边界吃掉；`ShowCursor(false)` 对 WinUI 输入线程持有的光标无效
  （HANDOFF §3 早已记录该限制）。
- **修法**：
  - native `mlrender.cpp`：子窗口创建时 `RegisterRawInputDevices`（`RIDEV_INPUTSINK`）；
    `WM_INPUT` 里读 `RAWMOUSE.lLastX/lLastY`，锁定期间直接 `MS_SendMouseMove(dx, dy)` 并吞掉消息；
  - `WM_SETCURSOR` 在锁定期间 `SetCursor(nullptr); return TRUE`（原生窗口才能真正隐藏光标）；
  - 新导出 `ML_SetPointerLock(int)`：锁定/解锁时切状态并立刻置空/恢复光标；
  - C#：`VideoSurface.SetPointerLock` 封装；`TryEnterPointerLock` / `ExitPointerLock` 调用它；
    锁定期间停用 C# 侧 `SendPointerDeltaPhysical`（否则与 Raw Input 双发、速度翻倍）。
- 未锁定仍走绝对坐标（桌面操作不变）。

### 1.0.53 —— 游戏鼠标：夹住指针 + 定时压制光标

- **现象（1.0.52 后）**：视角速度正常，但光标仍显示；窗口化时鼠标移出画面后视角就不动了。
- **原因**：`SetCursor(nullptr)` 只在 `WM_SETCURSOR` 触发时生效，鼠标不动就不再调用，WinUI 输入线程会把光标显示回来；
  指针没有被限制在画面内，一旦移出子窗口就收不到后续移动。
- **修法**（`native/mlrender.cpp`，`ML_SetPointerLock`）：
  - 锁定：`ClipCursor(子窗口屏幕矩形)` + `SetCursorPos(中心)` + `SetCursor(nullptr)` + `ShowCursor(false)` 压到负值；
    并 `SetTimer(child, 7, 20)`。
  - `WM_TIMER wp==7`：每 20ms 重新 `SetCursor(nullptr)` + 钉回中心（ClipCursor 保证不越界）。
  - 解锁：`KillTimer` + `ClipCursor(nullptr)` + 恢复 `ShowCursor(true)` + 恢复箭头光标。

### 1.0.54 —— 游戏鼠标：透明光标 + 重连后重建锁定

- **现象（1.0.53 后）**：光标仍显示；断开重连后视角又几乎不动。
- **原因**：
  1. `SetCursor(nullptr)` / `ShowCursor(false)` 都压不住 WinUI 输入线程的光标（它会把箭头画回来）；
  2. 重连会**重建视频子窗口**，Raw Input 的 `hwndTarget` 与定时器还挂在旧窗口上，等于锁定失效。
- **修法**（`native/mlrender.cpp`）：
  - 新增 `InvisibleCursor()`：`CreateIconIndirect` 出一个 32×32 全透明的 `HCURSOR`；
    锁定期间一律 `SetCursor(InvisibleCursor())`（WM_SETCURSOR / WM_TIMER 两处），视觉上等同隐藏且不会被覆盖；
  - `ML_HostChildWin32` 创建子窗口后：若 `g_pointerLock` 仍为真，**重新注册 Raw Input**、重新 `ClipCursor`、重挂 20ms 定时器；
  - 解锁仍 `ClipCursor(nullptr)` + `ShowCursor(true)` + 恢复箭头。

### 1.0.55 —— 游戏鼠标：点击由原生触发 + 主机光标同步

- **关键发现**：视频区是原生子窗口，**WinUI 的 `VideoPanel` 收不到点击**，所以 C# 的
  `TryEnterPointerLock()` 从来没被触发过 —— 前几版"点了没反应"根因在此。
- **修法**：
  - `native/mlrender.cpp`：`WM_LBUTTONDOWN` 里直接 `ApplyPointerLockState(true)`（点画面即锁定）；
    键盘钩子里 `VK_ESCAPE` 且已锁定时解锁；新增导出 `ML_GetPointerLock()`。
  - `ApplyPointerLockState()` 合并了 ClipCursor / 20ms 定时器 / 透明光标 / ShowCursor 计数，
    重连重建子窗口时自动重新挂载。
  - C# `StreamWindow`：性能定时器（0.5s）轮询 `_video.GetPointerLock()`，
    与 `_pointerLocked` 不同步时更新并 `ApplyRemoteCursorMode()` —— **主机端的光标模式跟着切**，
    否则主机会继续在画面里画光标（这才是"没隐藏"的真正来源）。

### 1.0.56 —— 解锁快捷键从 Esc 改为 Ctrl+Alt+Shift+Q

- **现象**：游戏里按 Esc 打开暂停菜单，关掉菜单后光标又出现、要再点一下画面才隐藏。
- **原因**：1.0.55 把 Esc 当成"解锁快捷键"，而 Esc 在游戏里是暂停菜单 —— 按下即解锁，
  菜单关闭后自然就回到未锁定状态。
- **修法**：`native/mlrender.cpp` 的键盘钩子里删除 Esc 解锁，改为
  `Ctrl+Alt+Shift+Q`（与 C# 侧 `VideoPanel_KeyDown` 的约定一致），并吞掉该组合不发给主机。
  其余按键（含 Esc）照常转发，游戏暂停菜单行为不受影响。

### 1.0.57 —— 游戏内 UI（暂停菜单）自动放开光标

- **现象**：游戏中按 Esc 打开暂停菜单，光标仍是隐藏的，菜单点不了。
- **原因**：锁定是"无条件"的 —— 不区分"游戏在转视角"和"游戏在显示 UI"。游戏显示 UI 时会自己把主机光标
  设成可见，我们却仍在 `ClipCursor` + 归中 + 透明光标。
- **修法**（`native/mlrender.cpp` 的 20ms 定时器）：
  - 每拍用 `MS_CursorInfo()` 看主机光标是否可见（`visible && id >= 0`）；
  - 可见 → 进入**软暂停**：`ClipCursor(nullptr)`、恢复箭头光标；`ML_GetPointerLock()` 此时返回 0，
    C# 的状态同步会把主机光标模式切回"主机画光标"，菜单可用鼠标点；
  - 主机光标再次隐藏 → 自动恢复锁定（重新 `ClipCursor` + 透明光标 + 归中）；
  - `WM_INPUT` 在软暂停时不再发相对位移，改走绝对坐标（菜单点击正确）。
- `ApplyPointerLockState(false)` 会复位软暂停标志。

### 1.0.58 —— 游戏 UI 软暂停时显示可见光标

- 现象：暂停菜单里鼠标能点但看不到光标（盲控）。
- 原因：WM_SETCURSOR 的锁定分支只判断 g_pointerLock，没判断软暂停，鼠标一动又被设回透明光标。
- 修法：WM_SETCURSOR 改为 g_pointerLock && !g_lockSuspended；20ms 定时器的软暂停分支每拍 SetCursor(IDC_ARROW)。

### 1.0.60 —— 锁定状态语义修正（暂停菜单光标）

- 现象：暂停菜单仍看不到光标（盲控）。
- 根因：`ML_GetPointerLock()` 在软暂停时返回 0，C# 状态同步判定"没锁定"，于是从未把主机光标模式切到可见（日志证据：只有 `wantLocal=True locked=False`，没有锁定时的切换）。
- 修法：`ML_GetPointerLock()` 返回真实锁定状态（含软暂停）；C# 在 `_pointerLocked=True` 时 `wantLocal=False` → `SetCursorMode(0)`，由主机/游戏决定是否画光标。
- 验证日志：`cursorMode wantLocal=False rc=0 locked=True` + `lockdbg: vis=1 id=1 susp=1`。
- 调试日志：原生每 5s 输出 `lockdbg: vis/id/susp`（保留，便于后续排查）。

### 1.0.61 —— 去掉"软暂停"：锁定始终有效

- 现象：退出暂停菜单后视角又几乎不动；菜单里出现"远程光标"（主机画的，符合预期但延迟）。
- 原因：软暂停依据主机光标可见性，菜单关闭后主机仍报 `visible=1`，`g_lockSuspended` 卡在 true → `WM_INPUT` 不再发相对位移。
- 修法：删除软暂停逻辑。锁定期间恒定为：本地透明光标 + `ClipCursor` + 20ms 归中；
  光标可见性完全交给主机（`SetCursorMode(0)`，游戏 UI 会自己画）。这样退出菜单视角立刻恢复。

### 1.0.62 —— 修复 ClipCursor 把系统鼠标锁死

- 事故：锁定后点开始菜单，鼠标被钉在屏幕中间、全系统无法操作。
- 原因：`ClipCursor` 是**全局**的；`ApplyPointerLockState(false)` 开头有 `if (!g_host.child) return;`，
  子窗口已销毁时直接返回，永远不解夹；C# 的 `ExitPointerLock` 在 `_pointerLocked == false` 时也提前返回。
- 修法：
  1. 解锁分支提到最前面，**无条件** `ClipCursor(nullptr)` + 恢复 `ShowCursor` 计数（不依赖子窗口）；
  2. `WM_KILLFOCUS/WM_CAPTURECHANGED` 时若在锁定则自动解锁（点开始菜单/切窗口立即解放鼠标）；
  3. C# `ExitPointerLock` 在未锁定分支也调用一次 `SetPointerLock(false)`（幂等）。
- 应急手段：另起进程 `ClipCursor(NULL)` 即可解锁（本次事故就是这么恢复的）。

### 1.0.63 —— 不再用 ClipCursor（系统级锁定事故的根治）

- 事故：锁定后鼠标被钉死，连开始菜单都点不到（ClipCursor 是全局裁剪，窗口化时指针出不了画面区域）。
- 修法：锁定改用 `SetCapture(子窗口)` + 透明光标 + Raw Input；**完全不用 ClipCursor**，也不再每 20ms `SetCursorPos` 归中。
  - 指针随物理鼠标自由移动（能移到任务栏），但点击被 capture 转发给主机；
  - 失去 capture / 失焦时自动解锁；快捷键 `Ctrl+Alt+Shift+Q` 解锁。
- 教训：**任何全局状态的 API（ClipCursor / 全局钩子）都必须有"无论窗口是否还在都能解除"的路径**。

### 1.0.65 —— 锁定期间把指针夹在串流窗口内（但留逃生通道）

- 需求：窗口化时鼠标不应移出串流画面。
- 做法：20ms 定时器用 `SetCursorPos` 把指针夹回窗口内（**不用全局 ClipCursor**，解锁/退出即刻恢复自由）；
  同时键盘钩子在锁定期间**放行 Win 键** —— 按 Win 打开开始菜单 → 失焦 → 自动解锁，避免再次出现"鼠标出不来"。
- 未锁定/解锁后指针完全自由。

### 1.0.66 —— Win 键仍发给主机（打开远程开始菜单）

- 澄清：用户说的"点开始菜单"是**远程桌面**的开始菜单（主机上的），不是本地的。
- 修正 1.0.65 的错误：删除"锁定期间放行 Win 键"的分支，Win 键恢复为发给主机并被本地吞掉。
- 保留 1.0.65 的指针夹取（SetCursorPos 到窗口内，非全局 ClipCursor）；
  退出锁定仍用 `Ctrl+Alt+Shift+Q` 或失焦（点别的本地窗口/Alt+Tab 到本地程序）。

### 1.0.67 —— 退出游戏自动解锁

- 需求：游戏中鼠标固定在窗口内；**退出游戏后自动解锁**。
- 判据：20ms 定时器里 `MS_CursorInfo()`，若主机光标**持续可见 ≥2s**（100 拍）→ `ApplyPointerLockState(false)`。
  游戏内 UI（暂停菜单）通常短暂显示光标，不会触发；回到主机桌面后光标一直可见 → 2 秒后自动解锁。
- 同时保留：指针夹在窗口内（`SetCursorPos`）、`Ctrl+Alt+Shift+Q` 手动解锁、失焦解锁。

### 1.0.68 —— 用主机应用状态判断"退出游戏"（暂停菜单不再误解锁）

- 需求修正：暂停菜单里**不能**解锁（游戏仍在运行），只有真的退出游戏才解锁。
- 移除 1.0.67 的"主机光标可见 ≥2s 自动解锁"（暂停菜单停留久了会被误判）。
- 改为 C# 每 3s 查 `HostApi.GetCurrentGame()`：返回 0（无应用）或与启动时的 `_appId` 不同 → `ExitPointerLock("app exited")`。
- 保留：游戏中指针夹在窗口内、`Ctrl+Alt+Shift+Q` 手动解锁、失焦解锁、暂停菜单由主机画光标。

### 1.0.69 —— 解锁键改回 Esc

- 按用户要求：锁定中按 **Esc** 解锁（Esc 仍同时发给主机，游戏菜单正常）。
- `Ctrl+Alt+Shift+Q` 保留为备选（该组合不发给主机）。
- 其余保持 1.0.68：指针夹在窗口内、锁定用远程光标、主机应用结束 3s 内自动解锁。

### 1.0.70 —— Parsec 式指针锁定（ClipCursor 到画面 + 四重解锁保障）

- 需求：指针必须锁在画面内、绝不飘出去。
- 做法：锁定期间 `ClipCursor(子窗口矩形)`，并在 20ms 定时器里**每拍重设**（防止被其他程序重置）。
- 解锁保障（关键，避免重演 1.0.62 的锁死事故）：
  1. Esc（锁定中，同时仍发给主机）；
  2. 窗口失焦 / WM_CAPTURECHANGED；
  3. 主机应用结束（C# 每 3s 查 currentgame）；
  4. 进程退出（Windows 自动重置 clip）。
- 保留 SetCapture 与透明光标；本地指针不可见。

### 1.0.71 —— 补 Ctrl+Alt+Shift+Z 解除捕获 + Raw Input 兜底

- A：新增 `Ctrl+Alt+Shift+Z` 解除输入捕获（Moonlight 约定，比 Esc 安全、不碰游戏菜单），该组合不发给主机。
- B：`RegisterRawInputDevices` 失败时（`g_rawMouse == false`），20ms 定时器退回 SDL 的 warp 模式：
  `GetCursorPos` → 与窗口中心差值 → `MS_SendMouseMove` → `SetCursorPos(中心)`。
- 参考：moonlight-qt `setCaptureActive()`（SDL_SetRelativeMouseMode）+ rustdesk PR #13928（ClipCursor + 光标控制）。

### 回退 1.0.71（2026-11-26）

- 按用户要求回到 1.0.70：移除 `Ctrl+Alt+Shift+Z` 解除捕获与 Raw Input 的 warp 兜底，
  保留 Esc / Ctrl+Alt+Shift+Q / 失焦 / 应用结束四条解锁路径与 ClipCursor 锁定。
- VERSION 现为 1.0.70。

### 1.0.72 —— 游戏内 UI 自动切"本地光标 + 绝对坐标"（跟手）

- 需求：游戏内各种界面（抽卡页、菜单、Alt 解锁）会显示光标，远程光标不跟手；且不能靠 Esc 切换。
- 方案（业界做法，见 moonlight-qt #1016 / Parsec "Always Relative"）：双模式自动切换。
  - 主机光标隐藏（游戏中）→ 相对模式 + Raw Input（视角）；
  - 主机光标可见（UI）→ `SetCursorMode(1)` 由客户端画光标 + C# 发绝对坐标（零延迟跟手）。
- 实现：原生 `MS_CursorInfo` 每 20ms 判定，`ML_GetGameUiMode()` 导出；UI 模式时 `WM_INPUT` 不发相对；
  定时器用 `BuildCursorFromHost()` 画主机形状光标。C# 轮询该状态切 `wantLocal` 并在 UI 模式走 `SyncAbsoluteToSystemCursor()`。
- 手动覆盖：`Ctrl+Alt+Shift+C` 切换 UI 模式（`g_manualUiModeSet`）。
- 注：切换瞬间主机会把光标吸到本地指针位置（已知代价）。

### 1.0.73 —— UI 模式在 WM_SETCURSOR 里也画本地光标

- 1.0.72 只在 20ms 定时器里画，鼠标一动 `WM_SETCURSOR` 又设回透明光标 → 看不到。
- 修：`WM_SETCURSOR` 的锁定分支按 `g_gameUiMode` 分流 —— UI 模式 `BuildCursorFromHost()`（主机形状）或箭头，游戏模式透明。
- 日志验证：`uiMode=True` + `cursorMode wantLocal=True locked=True`（主机不画、本地画）。

### 1.0.74 —— 修复 UI 模式光标"画不出来"的真因：ShowCursor 计数

- 根因：锁定进入时 `ShowCursor(FALSE)` 循环把显示计数压成**负数**；此后即使 `SetCursor(IDC_ARROW)` 也不会显示。
- 修法：新增 `EnsureCursorVisible(bool)`：UI 模式把计数恢复到非负，游戏模式压回负数；
  两处（WM_SETCURSOR / 20ms 定时器）统一调用，UI 模式直接画系统箭头（不再依赖主机形状位图，避免空形状）。
- 已验证模式切换日志：uiMode=True / cursorMode wantLocal=True locked=True。
