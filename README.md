<p align="center">
  <img src="Original%20Code/assets/gainmap-app.png" width="112" alt="GainMap icon">
</p>

# GainMap

把 NVIDIA HDR 截图（JXR）转换成 **ISO 21496-1 Gain Map JPEG**。普通 JPEG 查看器显示 SDR 图像，支持该 Gain Map 格式的 HDR 查看器可还原 HDR 效果。

Windows 原生 WPF 界面，浅色磨砂材质、连续曲率圆角。转换在本机完成，无需 hdrfix，也不上传图片。

## 功能

- 拖入 JXR，或点击中央按钮打开 Windows 文件选择器。
- 批量队列，添加、移除和清空图片。
- 输出到原图目录或指定文件夹；保留原始 JXR，覆盖已有输出前确认。
- JPEG 输出质量 1–100，默认 95，自动保存设置。
- 转换进度、失败详情，以及命令行转换入口。

## 使用

面向 Windows 11 x64。独立应用 `GainMap.exe` 内嵌转换组件，运行时无需安装 Python 或 hdrfix。

**[下载 GainMap.exe](https://github.com/laxy-vac/GainMap/releases/latest/download/GainMap.exe)** · [完整发布页与第三方许可证](https://github.com/laxy-vac/GainMap/releases/latest)

下载后双击即可运行。分发该应用时，请同时保留发布页中的第三方许可证包。

1. 打开 `GainMap.exe`。
2. 拖入 NVIDIA 的 HDR JXR 截图，或点击中央选择按钮。
3. 设置输出目录和质量，点击“开始转换”。

质量数值同时影响 SDR 主图和 Gain Map 的 JPEG 压缩；100 仍然是有损压缩。HDR 显示还取决于查看器、设备和系统；社交平台重新压缩图片时可能丢失 Gain Map。

## 支持范围

当前后端针对没有嵌入 ICC／Exif 色彩信息的 NVIDIA **浮点 scRGB JXR**。不猜测整数 JXR、带配置文件图片或透明图片的 HDR 编码方式；不支持的输入会显示错误。

输出使用 ISO 21496-1 元数据，未额外写入旧版 Ultra HDR XMP。因此仅支持旧版 XMP Gain Map 的查看器可能只显示 SDR。

色彩处理采用 scRGB 的 `1.0 = 80 nit`，先转换到线性 Rec.2020，再按 libultrahdr 的线性参考白编码，避免把 HDR 高光直接裁剪成 SDR。

## 从源码构建

需要 Windows x64、Python 3.12 x64，以及 Windows 已有的 .NET Framework WPF 编译器。依赖只安装到项目内虚拟环境：

```powershell
python -m venv .venv-hdr
& '.\.venv-hdr\Scripts\python.exe' -m pip install --only-binary=:all: -r '.\Original Code\requirements-build.txt'
& '.\Original Code\build_windows_exe_dragdrop.bat'
```

结果为 `release/GainMap.exe`。构建脚本不会自动安装软件、修改全局 Python 或 PATH。完整版本清单、验证记录和架构说明见 [BUILD_ENVIRONMENT.md](BUILD_ENVIRONMENT.md)。

源码位于 [`Original Code/`](Original%20Code/)。GUI 由 C#／WPF 实现；Python 编码进程使用 NumPy、imagecodecs 和 libultrahdr，并通过 PyInstaller 打包后嵌入 GUI。

## 命令行

```powershell
.\GainMap.exe --convert input.jxr output.jpg
.\GainMap.exe --self-test input.jxr .\validation-output
```

`--convert` 不覆盖已有输出；`--self-test` 在指定目录生成验证文件和报告。界面偏好保存在用户目录 `.hdrjxr_converter_config.json`；内嵌组件缓存于 `%TEMP%\GainMap`。

## 许可证

本项目原创源码按 [MIT](LICENSE) 发布。第三方依赖保留各自许可证，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。图标由 AI 生成。

This product includes Gain Map technology under license by Adobe.
