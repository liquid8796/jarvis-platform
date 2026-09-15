# Packaged PDF reader

The app uses the native PDFium library from NuGet package
`bblanchon.PDFium.Win32` version `153.0.7999`. Runtime assets are resolved by
.NET for Windows x64, x86 and arm64. PDF reading does not use a separately
installed viewer, Poppler, Python or a Codex runtime.

The accompanying LICENSE, VERSION and licenses directory were taken without
modification from the matching binary release:
https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/7999

Archive: https://github.com/bblanchon/pdfium-binaries/releases/download/chromium%2F7999/pdfium-win-x64.tgz

The package metadata identifies its build repository commit as
`95109aa15debd9128a955e2e24f72765211bf3f6`.
PDFium's own license and the third-party notices are distributed with every
build and publish under `ThirdParty/Pdfium`.

The adapter opens bytes in memory, reads each requested page's embedded text,
and renders its page graphics on an opaque white bitmap. It does not run PDF
JavaScript, open links, fetch external resources, or invent OCR output for
scanned pages. Scans are returned as actual rendered images.
