# Third-party notices

Optium+ File Manager includes the following OCR, PDF-rendering, and image-processing components. Their license texts are in `licenses/`.

## OCR engine and models

- **TesseractOCR .NET wrapper 5.5.2**, including the Tesseract 5.5.1 native engine: Apache License 2.0. Package: <https://www.nuget.org/packages/TesseractOCR/5.5.2>. Upstream source: <https://github.com/Sicos1977/TesseractOCR>.
- **Leptonica 1.85.0** native library: BSD 2-Clause License. Its notice is included in `licenses/Leptonica-BSD-2-Clause.txt`.
- **English and Russian trained data** (`eng.traineddata`, `rus.traineddata`) are from `tesseract-ocr/tessdata_fast`, pinned to commit `87416418657359cb625c412a48b6e1d6d41c29bd`; Apache License 2.0. The upstream license text is included in `licenses/tessdata-fast-LICENSE.txt`.

SHA-256 checksums for the trained data:

| File | SHA-256 |
|---|---|
| `tesseract/tessdata/eng.traineddata` | `7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2` |
| `tesseract/tessdata/rus.traineddata` | `E16E5E036CCE1D9EC2B00063CF8B54472625B9E14D893A169E2B0DEDEB4DF225` |

## PDF rendering

- **PDFiumCore 156.0.8076**, .NET bindings: Apache License 2.0. Package and source: <https://www.nuget.org/packages/PDFiumCore/156.0.8076>, <https://github.com/Dtronix/PDFiumCore>.
- **PDFium Windows x64 native library 156.0.8076** (`bblanchon.PDFium.Win32`): Apache License 2.0. Package and upstream release: <https://www.nuget.org/packages/bblanchon.PDFium.Win32/156.0.8076>, <https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium%2F8076>.

## Image processing

- **System.Drawing.Common 10.0.12**: MIT License. The package license text is included in `licenses/System.Drawing.Common-LICENSE.txt`; package: <https://www.nuget.org/packages/System.Drawing.Common/10.0.12>.

The project source redistributes the OCR language files and license texts in this directory. NuGet restores the pinned managed and native PDF/OCR components during build and bundles their runtime assets in the published executable. The Microsoft Visual C++ 2015–2022 x64 Redistributable is a separate prerequisite for the Tesseract native libraries and is not redistributed by this project.
