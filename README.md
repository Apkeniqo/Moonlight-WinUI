# Moonlight V+ WinUI 3 原型（技术验证）

> 目录：`D:\Projects\Moonlight WinUI3`（与 Qt 版 `D:\Projects\Moonlight-Qt-with-FluentUI` 完全独立，
> 只从后者复制了 `SDL2.dll` / `SDL3.dll` 做互操作验证）

> 目的：在决定「用 WinUI 3 重写」之前，先用最小代价验证三件事——
> ① WinUI 3 的工具链在本机能否跑通；② Mica / Mica Alt / 亚克力是否“天生就对”；
> ③ 能不能跟 Moonlight 现有的原生依赖（SDL2/FFmpeg 那套）互操作。
>
> 结论：**三件事全部成立**。但真正的难点不在这三件，见文末评估。

---

## 1. 运行

```powershell
# 用 Visual Studio 的 MSBuild，不要用 dotnet CLI（原因见「坑」）
& "D:\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
    MoonlightWinUI.csproj /p:Configuration=Debug /p:Platform=x64

.\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\MoonlightWinUI.exe
```

技术栈：.NET 8 + Windows App SDK 1.7（`WindowsAppSDKSelfContained=true`、无 MSIX，
直接跑 exe，不依赖机器上装的运行时）。

![原型截图](shot-final.png)

---

## 2. 验证到了什么

| 项 | 结果 |
|---|---|
| WinUI 3 工具链（VS 2026 / .NET 10 SDK / WinAppSDK 1.7） | ✅ 可编译可运行 |
| 无打包（unpackaged）+ 自包含运行时 | ✅ 直接双击 exe 启动 |
| **Mica / Mica Alt / 亚克力 / 纯色** 四种材质 | ✅ `MicaBackdrop{Kind=Base/BaseAlt}`、`DesktopAcrylicBackdrop`、`null` + 实色；切换即时生效 |
| **原生标题栏 + 内容延伸** | ✅ `ExtendsContentIntoTitleBar` + `SetTitleBar`：标题区自己画，最小化/最大化/关闭仍由系统绘制 |
| 导航布局 | ✅ `NavigationView`（展开式侧栏、选中指示条） |
| **原生互操作** | ✅ P/Invoke 调 Moonlight 仓库里的 `SDL2.dll` → `SDL 2.32.74`（sdl2-compat，需要旁边的 `SDL3.dll`） |
| UI 帧率 | 167 fps（1500×900 窗口，空载） |

这正是 Qt 版本折腾了一整天没稳住的部分：WinUI 3 里 Mica 是**框架原生能力**，
不需要自己 `DwmSetWindowAttribute`、不需要透明 swapchain、不需要处理
`WM_NCCALCSIZE`，标题栏按钮也天然是系统的。

---

## 3. 两个坑（都已绕过）

1. **`dotnet build` 缺 PriGen 任务**
   `Microsoft.Build.Packaging.Pri.Tasks.dll` 只在 VS 的 MSBuild 里有，dotnet SDK 里没有，
   会报 `MSB4062`。→ 用 `MSBuild.exe` 构建（或装 VS 的 Windows 应用开发工作负载）。
2. **sdl2-compat 缺 SDL3 会“假死”**
   Moonlight 的 `SDL2.dll` 是 sdl2-compat，内部要加载 `SDL3.dll`。只拷 SDL2.dll 时，
   首次调用会卡住（看起来像死锁）。→ 两个 DLL 必须一起放。

另外：`SelectedIndex` 在 XAML 里预设时，`SelectionChanged` 会在 `InitializeComponent()`
**期间**触发，此时 `x:Name` 元素还没就绪，需要判空。

---

## 4. 还没验证的（决定成败的那部分）

1. **视频呈现**：Moonlight 用 SDL / D3D11 直接渲染到自己的窗口。WinUI 3 里没有 `HwndHost`，
   两条路：
   - 把解码输出接到 `SwapChainPanel`（需要自定义 D3D11 渲染器，`d3d11va.cpp` 要改造成
     渲染到外部 swapchain）；
   - 或者保留独立顶层无边框窗口做视频层，WinUI 只做壳（丑，但工程量小）。
   **这是整个重写里最大的一块，原型没碰。**
2. **输入**：SDL 的键鼠/手柄事件循环要挪到 WinUI 的消息泵里，手柄热插拔、DS5 触觉、
   触控板这些都要重接。
3. **音频**：SDL 音频回调与 WinUI 线程模型无关，但设备热切换要重做。
4. **串流中的材质性能**：Mica 在视频播放时的合成开销没测。

---

## 5. 评估：值不值得重写

| 维度 | 现状（Qt/QML） | WinUI 3 重写 |
|---|---|---|
| 代码量 | UI 58 个 QML + 约 100 个 C++ | 全部 UI 与胶水层重写，核心 C++ 保留（需 C++/WinRT 桥） |
| 平台 | Windows / macOS / Linux / Steam Link | **仅 Windows** |
| 材质/标题栏 | 要自己碰 DWM，坑多 | 框架原生 |
| 工期（功能对齐） | — | **2–4 个月**（一人） |
| 生态 | Qt 6.9.3 + qmake，现有 CI 四平台 | .NET 8 + WinAppSDK + MSIX，需要新的 CI |

**建议**：
- 如果目标就是「Windows 上一个体验最好的 Moonlight 客户端」→ WinUI 3 值得，
  但要接受丢掉 macOS/Linux，并且先做第 4 节的视频呈现验证（1–2 周的专项 spike）。
- 如果要保住四平台 → 留在 Qt，把 WinUI 3 的布局规范照搬（NavigationView、Card、
  原生标题栏），材质上接受“标题栏 Mica + 内容纯色”的折中。

---

## 6. 第二轮验证：D3D11 视频面（关键通路）

![视频面](shot-video3.png)

在 `SwapChainPanel` 里跑通了真实管线：

```
ID3D11Device (BgraSupport, FL11.1/11.0)
  → IDXGIFactory2::CreateSwapChainForComposition
      (B8G8R8A8, FlipSequential, 2 buffers, Stretch)
  → ISwapChainPanelNative::SetSwapChain
  → 每帧 ClearRenderTargetView + Present(1, 0)
```

实测（1500×860 窗口，面板 1339×476）：

| 指标 | 数值 |
|---|---|
| 合成帧率 | **165 fps**（跟随 165Hz 显示器 vsync） |
| Draw + Present 耗时 | **0.71 ms** |
| Mica 同时开启 | 是（窗口整体 Mica，视频面不透明覆盖其上） |

**这意味着「Moonlight 的 D3D11 解码输出能不能进 WinUI 3 窗口」这条路是通的。**
真正接流时只需要把 `ClearRenderTargetView` 换成「解码后的 NV12 纹理 → 简单像素着色器上屏」，
渲染器和 swapchain 的骨架不用改。

### 还没做的（下一步）

1. **窗口/面板尺寸变化**：现在交换链按首次尺寸创建，`ResizeBuffers` 还没接；
2. **NV12 → RGB 上屏**：需要 PS + 视频处理器（`ID3D11VideoProcessor` 或手写 YUV 着色器）；
3. **把 `d3d11va.cpp` 的纹理输出接到这里**：Moonlight 现在的渲染器是 `ID3D11Device` +
   自己的 swapchain，需要改成「复用同一 device，渲染到外部 swapchain」；
4. **帧节奏**：现在用 `CompositionTarget.Rendering` 驱动，真串流要按解码时间戳调度
   （Moonlight 有 pacer，需要移植或重写）。
---

## 7. 第三轮验证：NV12 上屏 + 窗口缩放

![NV12 视频面](shot-nv12.png)

把「清屏」换成真实的 NV12 上屏管线：

```
CPU 生成 NV12（Y 960×540 R8_UNORM + UV 480×270 R8G8_UNORM，Dynamic + WriteDiscard）
  → Map/Unmap 逐行拷贝
  → 全屏三角形（SV_VertexID，无顶点缓冲）
  → PS：BT.709 limited range → RGB
  → flip-model 合成交换链 → SwapChainPanel → Present(1, 0)
```

实测（面板 1000×476，视频 960×540）：

| 指标 | 数值 |
|---|---|
| 帧率 | **135–165 fps** |
| CPU 生成 + 上传 + Draw + Present | **4.7 ms / 帧**（其中大部分是 CPU 合成测试图，真解码时会省掉） |
| 窗口缩放 | ✅ `ResizeBuffers` 重建后台缓冲，画面不花不卡 |

### 这一轮踩的两个坑

1. **ID3DBlob 不能按字段读**：`ID3DBlob` 是 COM 接口，`size` 不在固定偏移上，
   必须从 vtable 调 `GetBufferPointer`（第 4 个槽）和 `GetBufferSize`（第 5 个槽），
   否则 `CreateVertexShader` 直接 `E_INVALIDARG`。
2. **NV12 的 UV 行宽**：UV 平面每个采样 2 字节，行宽是「像素宽」而不是「采样数」。
   按 480 字节拷 960 字节的行，等于每行错位一半，颜色会逐渐跑偏（第一版彩条就是花的）。

### 还没做

- 接真实解码帧（`d3d11va.cpp` 的纹理输出）
- 帧节奏（按解码时间戳调度，替代 `CompositionTarget.Rendering`）
- HDR（10-bit / P010）这条路径没验证
---

## 8. 第四轮验证：真实 d3d11va 硬解（第三步完成）

![硬解回放](shot-decode3.png)

`native/mlrender.cpp` 现在跑的是真实解码链路：

```
test-1080p60.h264（Annex-B，1920×1080@60）
  → avcodec + AV_HWDEVICE_TYPE_D3D11VA（复用 C# 建的 ID3D11Device/Context）
  → 解码出的 ID3D11Texture2D (NV12, array slice)
  → CopySubresourceRegion 到自己的 SHADER_RESOURCE 纹理
  → 平面 SRV（Y: R8 / UV: R8G8）→ BT.709 PS → swapchain → SwapChainPanel
```

实测：`d3d11va 1920×1080 → 面板 1000×476，165 fps，已解 1570 帧`（播放结束自动循环）。

### 这一轮踩的坑（都是 Moonlight 预编译 FFmpeg 的特性）

1. **没有 `file` 协议**：`avformat_open_input` 报 `Protocol not found`。
   真接流本来也不需要 demuxer —— 改成直接喂 Annex-B 裸流。
2. **没有编译 `av_parser`**：`av_parser_init()` 返回 null。手写了 start-code 切分，
   每个 NAL 当一个 packet 送进解码器（真接流时 Moonlight 给的就是完整访问单元，更不需要 parser）。
3. **解码纹理没有 `SHADER_RESOURCE` 绑定**：直接建 SRV 会失败、画面全黑。
   每帧 `CopySubresourceRegion` 到自己的 NV12 纹理再采样。
4. **NV12 的平面 SRV 要 D3D11.3**：`D3D11_SHADER_RESOURCE_VIEW_DESC1` +
   `ID3D11Device3::CreateShaderResourceView1`（`PlaneSlice` 只在 DESC1 里），
   返回值类型是 `ID3D11ShaderResourceView1**`。

### 下一步

- 把文件源换成 Moonlight session 的 H.264 包（`moonlight-common-c` 的解码回调）
- 用 QPC 替代 `GetTickCount` 计时（现在 15ms 是 15.6ms 粒度造成的假值）
- 帧节奏按解码时间戳调度（现在是“每个 UI tick 解一帧”，所以 60fps 片子跑成了 165fps）
---

## 9. 第五轮：网络包接口 + 真帧节奏

![按 60fps 节奏播放](shot-stream.png)

把"读文件 → 画"改成和真实串流同构的结构：

```
[网络线程 / feeder]  ML_PushPacket(access_unit, len)  ──▶  std::deque（上限 8，背压丢旧）
[UI 线程]            ML_Frame()（每 tick）           ──▶  取包 → 解码 → 上屏
```

- `ML_PushPacket()` **就是 moonlight-common-c 的解码回调位置**：真接流时把 `Dr` 回调里的
  `data/len` 直接转给它即可，其余代码不动。
- feeder 线程按 `fps` 参数定时推包，模拟网络到达节奏（测试流 481 个访问单元，60fps）。
- 帧节奏不再由 UI 刷新率决定：`ML_Frame` 只处理队列里已有的包，队列空就什么都不做。

实测（窗口 1400×860，面板 1000×476）：

| 指标 | 数值 |
|---|---|
| 解码 + 上屏 | **0.37 ms/帧**（QPC 实测，之前 15ms 是 GetTickCount 粒度造成的假值） |
| 入队 / 丢包 | 822 / 0 |
| 队列深度 | 0（生产与消费速率一致，无积压） |
| 分辨率 | 1920×1080 → 面板 1000×476，d3d11va 硬解 |

### 剩下唯一一块

把 feeder 换成真的 session：

1. `moonlight-common-c` 的 `LiStartConnection` + 配对（PIN）
2. 视频回调里 `ML_PushPacket(data, len)`（音频走 SDL）
3. 输入（SDL 手柄/键鼠）转成 Moonlight 的 input packet

到这一步，`mlrender.dll` 一行不用改。
---

## 10. 接真串流（交接）

下一步的完整交接文档在 **[HANDOFF.md](HANDOFF.md)**，里面记了：

- 本机就是 Sunshine 主机（47984/47989/48010 都在监听），测试走环回
- Qt 客户端的配对身份已导出到 `native/creds/`（**不需要重新配对**）
- `moonlight-common-c.lib` 已经编译好（`native/mlc-build/`）
- `mlsession.cpp` 的完整设计：OpenSSL HTTPS → `/serverinfo` → `/launch` →
  `LiStartConnection` + 回调签名 + rikey 时序
- 构建命令、7 个会浪费时间的坑、验证步骤

开新会话时先读 `HANDOFF.md`，然后直接写 `native/mlsession.cpp`。