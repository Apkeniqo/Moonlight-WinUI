# native\redist\x64

WinUI 应用运行期要用的 DLL，随仓库同步（两台机器都自带），由 `MoonlightWinUI.csproj`
整目录拷到输出目录。有了它，C# 侧构建不再依赖 `Moonlight-Qt-with-FluentUI` 的绝对路径。

| 文件 | 用途 |
|---|---|
| `avcodec-63.dll` `avformat-63.dll` `avutil-61.dll` `swscale-10.dll` `dav1d.dll` | FFmpeg（mlrender 的 d3d11va 解码） |
| `libssl-3-x64.dll` `libcrypto-3-x64.dll` | OpenSSL 3（mlsession 的 HTTPS；MLC 也用） |
| `opus.dll` | libopus（mlaudio 的 Opus 多流解码） |

## 换版本时

- **整组替换**，别只换一个：FFmpeg 那几个 DLL 之间、`avcodec-63` 与链接时用的
  `avcodec.lib`（随仓库提供，在 `native\third_party\libs\windows\lib\x64`）版本必须对得上。
- 换完记一次 `native\mlrender.dll` 的重建（`native\build-native.bat`），
  否则头文件/导入库与运行期 DLL 可能对不上。
- 同步时这几个文件是二进制，确保两边的同步工具不会跳过它们。
