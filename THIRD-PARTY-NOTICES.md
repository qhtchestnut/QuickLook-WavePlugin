# 第三方组件与许可

`.qlplugin` 安装包里分发了下列第三方组件，各自遵循其自身许可。本文件随包分发，以满足
这些许可的声明要求。

`QuickLook.Common.dll` **不包含**在安装包内：插件在运行时使用 QuickLook 宿主随附的那一份。
它只作为构建依赖（`QuickLook.Common/` 子模块）出现在源码仓库里。

---

## NAudio

- 主页：https://github.com/naudio/NAudio
- 随包文件：`NAudio.dll`、`NAudio.Core.dll`、`NAudio.Wasapi.dll`、`NAudio.WinMM.dll`、
  `NAudio.Asio.dll`、`NAudio.Midi.dll`、`NAudio.WinForms.dll`
- 许可：MIT
- 版权：Mark Heath 及贡献者

## NAudio.Vorbis

- 主页：https://github.com/naudio/Vorbis
- 随包文件：`NAudio.Vorbis.dll`
- 许可：MIT
- 版权：Mark Heath 及贡献者

## NVorbis

- 主页：https://github.com/NVorbis/NVorbis
- 随包文件：`NVorbis.dll`
- 许可：MIT
- 版权：Andrew Ward 及贡献者

## .NET 运行时库

- 主页：https://github.com/dotnet/runtime
- 随包文件：`System.Buffers.dll`、`System.Memory.dll`、`System.Numerics.Vectors.dll`、
  `System.Runtime.CompilerServices.Unsafe.dll`、`System.ValueTuple.dll`、
  `System.Security.AccessControl.dll`、`System.Security.Principal.Windows.dll`、
  `Microsoft.Win32.Registry.dll`
- 许可：MIT
- 版权：.NET Foundation 及贡献者

---

## MIT 许可证

上述 NAudio、NAudio.Vorbis、NVorbis 与 .NET 运行时库均以 MIT 许可证授权。以下条款对每一项
分别适用，版权归该项所列的版权持有人。

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
