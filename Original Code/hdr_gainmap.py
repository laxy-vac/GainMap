"""NVIDIA floating-point scRGB JXR -> ISO 21496-1 gain-map JPEG.

Uses imagecodecs' JPEG XR decoder and Google's libultrahdr encoder.
scRGB: https://learn.microsoft.com/windows/win32/direct3darticles/high-dynamic-range
Codec: https://github.com/google/libultrahdr
This product includes Gain Map technology under license by Adobe.
"""
from pathlib import Path
import os
import tempfile
import time
import struct

import imagecodecs
# Load the two required native codecs explicitly. imagecodecs normally loads
# codecs lazily and masks native import errors behind a delayed-import stub.
import imagecodecs._jpegxr
import imagecodecs._ultrahdr
import numpy as np

ISO_NAMESPACE = b"urn:iso:std:iso:ts:21496:-1"
XMP_NAMESPACE = b"http://ns.adobe.com/hdr-gain-map/1.0/"

# D65 linear Rec.709 -> linear Rec.2020 (derived from the primary chromaticities).
# scRGB negative RGB components can represent colors outside Rec.709. Convert
# before clipping, rather than discarding those wide-gamut colors in scRGB.
REC709_TO_REC2020 = np.array([
    [0.627403896, 0.329283038, 0.043313066],
    [0.069097289, 0.919540395, 0.011362316],
    [0.016391439, 0.088013308, 0.895595253],
], dtype=np.float32)


def decode_nvidia_jxr(source: Path) -> np.ndarray:
    """Return opaque linear scRGB pixels; reject formats with ambiguous encoding."""
    data = Path(source).read_bytes()
    if len(data) < 8 or data[:4] != b"II\xbc\x01":
        raise ValueError("不是受支持的 Windows JXR 容器。")
    offset = struct.unpack_from("<I", data, 4)[0]
    if offset + 2 > len(data):
        raise ValueError("JXR 元数据不完整。")
    count = struct.unpack_from("<H", data, offset)[0]
    if offset + 2 + count * 12 > len(data):
        raise ValueError("JXR 元数据不完整。")
    for index in range(count):
        tag, _kind, size, _value = struct.unpack_from("<HHII", data, offset + 2 + index * 12)
        if tag in (0x8773, 0x8769) and size:
            raise ValueError("图片有嵌入颜色配置／Exif，当前后端不猜测其色彩空间；请使用 NVIDIA 原始浮点截图。")
    # Microsoft's WIC rule for RGB float images without a color context is
    # scRGB. Profiled images need color management rather than this default.
    pixels = imagecodecs.jpegxr_decode(data)
    if pixels.dtype.kind != "f" or pixels.ndim != 3 or pixels.shape[2] not in (3, 4):
        raise ValueError("目前仅支持 NVIDIA 浮点 scRGB HDR JXR；不将整数 JXR 猜测为 HDR。")
    if not np.isfinite(pixels).all():
        raise ValueError("图片包含非有限像素，无法可靠编码 HDR。")
    if pixels.shape[2] == 4 and not np.allclose(pixels[..., 3], 1.0, atol=1e-4):
        raise ValueError("JPEG 不支持透明度，请使用不带透明区域的 HDR 截图。")
    return np.ascontiguousarray(pixels[..., :3], dtype=np.float32)


def prepare_hdr(scrgb: np.ndarray) -> tuple[np.ndarray, float]:
    """Normalize physical luminance to libultrahdr's 203-nit linear reference white."""
    linear2020 = scrgb @ REC709_TO_REC2020.T
    if float(linear2020.max()) > 125.0 or float(linear2020.min()) < -0.001:
        raise ValueError("图片超出 Rec.2020／10000 尼特编码范围；为避免静默丢失 HDR 信息，已停止转换。")
    # A JPEG gain-map image cannot express negative light. Only values outside
    # the representable Rec.2020 gamut are clipped; HDR values are not clipped
    # at SDR white. scRGB 1.0 is 80 cd/m²; linear codec 1.0 is 203 cd/m².
    # See hdrSampleToNitsFactor in libultrahdr/lib/src/jpegr.cpp. Its 10000-nit
    # reference display peak describes headroom, not the half-float pixel unit.
    np.clip(linear2020, 0.0, 125.0, out=linear2020)
    peak_nits = float(linear2020.max()) * 80.0
    rgba = np.ones((*linear2020.shape[:2], 4), dtype=np.float16)
    rgba[..., :3] = linear2020 * (80.0 / 203.0)
    return rgba, max(203.0, min(10000.0, peak_nits))


def encode_gainmap(scrgb: np.ndarray, quality: int = 95) -> tuple[bytes, dict]:
    if not 1 <= quality <= 100:
        raise ValueError("JPEG 质量必须在 1 到 100 之间。")
    rgba, peak_nits = prepare_hdr(scrgb)
    # Let the reference encoder create an aligned SDR rendition and gain map.
    # This never tries to manufacture HDR from an already tone-mapped PNG.
    encoded = imagecodecs.ultrahdr_encode(
        rgba, level=quality, scale=1,
        gamut=imagecodecs.ULTRAHDR.CG.BT_2100,
        transfer=imagecodecs.ULTRAHDR.CT.LINEAR,
        nits=peak_nits,
    )
    if not imagecodecs.ultrahdr_check(encoded) or ISO_NAMESPACE not in encoded:
        raise RuntimeError("编码器未生成有效的 ISO 21496-1 gain map JPEG。")
    info = {"width": rgba.shape[1], "height": rgba.shape[0],
            "peak_nits": round(peak_nits, 3), "iso21496": True,
            "ultrahdr_xmp": XMP_NAMESPACE in encoded,
            "codec": imagecodecs.ultrahdr_version(), "bytes": len(encoded)}
    return encoded, info


def convert_jxr_to_gainmap(source: Path, destination: Path, quality: int = 95) -> dict:
    source, destination = Path(source).resolve(), Path(destination).resolve()
    if destination.suffix.lower() not in (".jpg", ".jpeg"):
        raise ValueError("Gain map JPEG 的输出扩展名必须是 .jpg 或 .jpeg。")
    if source.resolve() == destination.resolve():
        raise ValueError("输出路径不能覆盖原始 JXR。")
    encoded, info = encode_gainmap(decode_nvidia_jxr(source), quality)
    # Publish a complete file only. A failed conversion preserves an existing
    # output instead of leaving a truncated JPEG behind.
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(prefix=".gainmap-", suffix=".tmp", dir=destination.parent,
                                         delete=False) as handle:
            temporary = Path(handle.name)
            handle.write(encoded)
        for attempt in range(6):
            try:
                os.replace(temporary, destination)
                break
            except PermissionError:
                if attempt == 5:
                    raise
                time.sleep(0.2)
    finally:
        if temporary is not None and temporary.exists():
            try:
                temporary.unlink()
            except PermissionError:
                pass  # Do not hide the conversion error if a scanner holds it.
    return info
