# Third-party notices

The MIT license in this repository applies to the original GainMap project
code. Dependencies retain their own copyrights, licenses and notices. This
source repository does not vendor their source code or compiled libraries.

The published Windows v1.0.0 application is accompanied by
[THIRD_PARTY_LICENSES.zip](https://github.com/laxy-vac/GainMap/releases/download/v1.0.0/THIRD_PARTY_LICENSES.zip),
containing license texts, an actual-component inventory and source references.
Retain this package when redistributing that binary.

## Runtime components

| Component | Purpose | Upstream licensing information |
| --- | --- | --- |
| [CPython](https://www.python.org/) | Embedded Python runtime in compiled builds | [PSF license and bundled notices](https://docs.python.org/3/license.html) |
| [NumPy](https://github.com/numpy/numpy) | HDR pixel processing | [BSD-3-Clause and bundled library notices](https://github.com/numpy/numpy/blob/main/LICENSE.txt) |
| [imagecodecs](https://github.com/cgohlke/imagecodecs) | JPEG XR decoding and Ultra HDR encoding bindings | [BSD-3-Clause](https://github.com/cgohlke/imagecodecs/blob/master/LICENSE), [bundled codec licenses](https://github.com/cgohlke/imagecodecs/tree/master/licenses) |
| [jxrlib](https://github.com/4creators/jxrlib) | JPEG XR codec used through imagecodecs | [BSD-2-Clause](https://github.com/4creators/jxrlib/blob/master/LICENSE) |
| [libultrahdr](https://github.com/google/libultrahdr) | SDR rendition and gain-map JPEG encoder | [Apache-2.0 or MIT](https://github.com/google/libultrahdr#license) |

## Gain Map technology

**This product includes Gain Map technology under license by Adobe.**

See Adobe's [Gain Map patent license](https://helpx.adobe.com/camera-raw/desktop/hdr-and-advanced-output/gain-map.html).

## Build tools and binary redistribution

PyInstaller and supporting build packages are listed in
`Original Code/requirements-build.txt`. PyInstaller's
[license exception](https://pyinstaller.org/en/stable/license.html) permits
distribution of bundled applications under their own applicable licenses.

Compiled builds embed CPython, NumPy and imagecodecs together with native
libraries selected by PyInstaller. Anyone redistributing a compiled build
must retain the license texts and notices from the **actual wheels and
runtime used for that build**, including any additional bundled codecs or
numerical libraries. This list and its links do not replace those license
texts. Inspect the build artifacts for the exact bundled components before
publishing a binary distribution.

Windows and .NET Framework system components are provided by the operating
system and are not vendored in this repository.
