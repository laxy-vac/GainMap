# GainMap 编译环境与构建说明

最初整理日期：2026-10-04；开源发布更新：2026-10-15。此文档记录已经成功构建的环境；不保留虚拟环境、下载包或中间产物。

2026-10-04 清理后，旧版 EXE 在无 `.venv-hdr` 的情况下通过了真实 JXR 转换、质量 95／60、队列和错误处理检查。2026-10-15 发布版剔除了内嵌微软运行库并增加缺失提示，复用已有编码组件及 Windows C# 编译器完成重打包，没有恢复虚拟环境或安装软件。当前正式应用：

```text
SHA-256: 19BA5781C2527A1128D173C037A74CCAA33220F09CED1FE2D3E0ACE2C02A5A86
```

## 保留内容

- `GainMap.exe`：本地保留的正式单文件应用，支持磨砂界面、文件选择／拖放、批量转换、输出目录、进度、失败详情，以及 1–100 的质量调整。源码构建默认输出到 `release/GainMap.exe`，应用不纳入 Git 源码历史。
- `Original Code/`：当前正式版本源码、构建脚本、依赖清单和必需图标资源。
- 本文档：编译环境及复建步骤。

源文件用途：

| 文件 | 用途 |
| --- | --- |
| `GlassApp.cs` | WPF 窗口逻辑、队列、质量设置、偏好保存、转换调度、命令行与离屏自检 |
| `GlassUi.xaml` | 磨砂界面、文件选择按钮、队列、质量滑块、进度条 |
| `GlassControls.cs` | 连续曲率圆角绘制 |
| `NativeFolderPicker.cs` | Windows 原生目录选择器 |
| `WorkerClient.cs` | 提取并复用内嵌编码进程，使用 UTF-8 JSON 通信 |
| `GainMap.manifest` | 普通用户权限、高 DPI 支持 |
| `gainmap_worker.py` | 编码进程入口；验证路径、覆盖许可与质量参数 |
| `package_worker.py` | 使用标准库重组编码组件，剔除微软 DLL、重定向私有 MSVCP 导入并更新 NumPy 文件记录 |
| `hdr_gainmap.py` | JXR 解码、色彩／亮度转换、Gain Map JPEG 编码与完整文件写入 |
| `build_windows_exe_dragdrop.bat` | 构建内嵌编码组件及最终 WPF EXE；不自动安装软件 |
| `requirements-hdr.txt` | 编码依赖版本 |
| `requirements-build.txt` | 完整构建依赖版本 |
| `assets/gainmap-app.png` | 嵌入界面的透明图标原图 |
| `assets/gainmap-app.ico` | Windows EXE 多尺寸图标，16–256 px |

PNG 和 ICO 是编译资源，不属于可清理缓存。图标由内置 imagegen 生成，使用磨砂冰蓝图片框与 HDR 高光图形；编译不需要重新生图。

## 已验证环境

| 项目 | 环境 |
| --- | --- |
| 操作系统 | Windows 11 x64，构建日志版本 `10.0.26300` |
| Python | CPython 3.12.10 x64 |
| Python 构建环境 | 项目根目录 `.venv-hdr`，清理时删除，复建时重新创建 |
| C# 编译器 | Windows 现有 .NET Framework 编译器，文件版本 `4.8.9221.0` |
| WPF PresentationFramework | 文件版本 `4.8.9347.0` |

C# 编译器路径：

```text
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

构建脚本使用同一 Framework64 目录下的以下现有程序集：

```text
WPF\PresentationFramework.dll
WPF\PresentationCore.dll
WPF\WindowsBase.dll
System.Xaml.dll
System.Web.Extensions.dll
System.Windows.Forms.dll
```

不需要 Visual Studio、CMake、Ninja 或另装 Windows App SDK。若目标机器缺少上述现有组件，先确认环境，不默认安装或改用其他运行时。

Python 包来自 PyPI，使用 Windows x64 预编译 wheel，版本已记录在 requirements 文件中：

```text
numpy==2.5.3
imagecodecs==2026.8.16
pyinstaller==6.22.3
pyinstaller-hooks-contrib==2026.8
altgraph==0.17.5
packaging==26.3
pefile==2024.8.26
pywin32-ctypes==0.2.3
setuptools==84.0.0
```

当前 imagecodecs Windows wheel 的 codec 自报版本为 `jxrlib 1.1` 和 `libultrahdr 2.0.2`；2026-10-15 发布检查发现 wheel 元数据将 jxrlib 列为 `1.2`，两者版本口径不同，未据此断言确切上游修订。`tkinterdnd2` 只用于此前废弃的 Tk 界面，不是当前正式版依赖。

## 复建步骤

以下是恢复编译环境的命令记录，本次清理不执行安装。重新创建环境及安装依赖应遵守用户的授权规则；仅操作项目内隔离环境，不向全局 Python 安装。

在项目根目录运行：

```powershell
python -m venv .venv-hdr
& '.\.venv-hdr\Scripts\python.exe' -m pip install --only-binary=:all: -r '.\Original Code\requirements-build.txt'
& '.\Original Code\build_windows_exe_dragdrop.bat'
```

Python 命令应指向已验证的 3.12 x64 环境。构建脚本本身只检查并复用环境，不升级 pip、不自动下载依赖，也不修改 PATH、注册表或系统设置。

构建过程：

1. PyInstaller 将 `gainmap_worker.py` 和编码依赖打包为 `GainMapWorker.exe`。必须包含 `imagecodecs._shared`、`_shared_cython`、`_jpegxr`、`_ultrahdr` 隐藏模块。
2. `package_worker.py` 剔除 Microsoft VC/UCRT/API-set DLL，将 NumPy 两处私有 MSVCP 导入改为系统 `msvcp140.dll`，并更新 wheel 文件记录。只使用标准库，不下载运行库。
3. Windows C# 编译器编译四个 C# 源文件，并嵌入 XAML、PNG 图标、处理后的编码 EXE 及应用 manifest；Windows 图标由 ICO 提供。
4. 最终生成 `release/GainMap.exe`。运行时无需项目虚拟环境、Python 安装或 hdrfix，但系统需已有 Microsoft Visual C++ v14 x64 运行库；缺失或过旧时请自行从 [微软官方页面](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) 获取。软件不自动安装，也不再分发这些 DLL。分发 EXE 时应一并保留发布页的第三方许可证包。

中间输出集中在 `.work-ui/worker-build`、`worker-dist`、`worker-spec`。复建会重新生成这些目录，发布时不包含它们。

## 使用与运行时文件

双击 `release/GainMap.exe`。点击中央按钮多选 JXR，或拖放文件；选择输出目录和质量后开始转换。默认质量 95，默认输出到原图目录，已有同名 JPEG 会先确认覆盖，原始 JXR 不删除。

运行时仅把内嵌组件缓存到系统临时目录的 `GainMap/<组件哈希>/`；编码组件启动时还会临时解包 Python 库。应用会在用户目录的 `.hdrjxr_converter_config.json` 保存输出目录与质量偏好。清理项目不会删除用户偏好，也不会扫描或清理项目外的缓存。

## 输出及验证

输出为 ISO 21496-1 Gain Map JPEG。普通 JPEG 解码器可读取 SDR 基础图，支持该标准的查看器可恢复 HDR。当前编码器不写旧 Ultra HDR XMP 双元数据；HDR 显示仍取决于设备、查看软件和传输平台是否保留增益图。

质量参数同时用于 SDR 基础图和增益图的 JPEG 压缩；100 仍不等于无损。样本质量 95 输出 1,979,610 字节，质量 60 输出 570,464 字节，两者都通过 HDR 解码检查。

已验证：独立目录中的单文件运行、WPF/XAML 离屏布局、原生目录选择器接口、队列去重、真实 JXR 转换、无效输入反馈、同名输出冲突、默认 95 与可调 60 的端到端参数传递。未启动桌面自动操作来验证交互；桌面外观和文件选择由用户检查。尚未在另一台电脑上实测。

2026-10-15 发布检查：重组后的 worker 不含微软 DLL（移除 48 项）；在生成的浮点 HDR JXR 上，质量 95／60 输出与旧 worker 逐字节相同，并通过 HDR 解码。实际 MSVCP／VCRUNTIME／UCRT 均从 `System32` 加载。新 EXE 的 9 项离屏自检全部通过。本次没有原始用户截图，使用生成样本检查重打包后的编码结果。

复建后，可使用自己的本地 JXR 验证：

```powershell
# 单张无界面转换；已有输出不会被覆盖
& '.\release\GainMap.exe' --convert 'input.jxr' 'output.jpg'

# 自检不打开窗口或文件选择器，也不读取／修改用户偏好
& '.\release\GainMap.exe' --self-test 'input.jxr' '.\validation-output'
```

自检会在指定输出目录生成 JPEG、测试素材与 `frontend-test.json`，应与源码和发布目录分开存放，验证结束后可清理。

This product includes Gain Map technology under license by Adobe.

This software is based in part on the work of the Independent JPEG Group.
