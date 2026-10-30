<p align="center"><img src="docs/icon.png" width="96" alt="Quickey icon"></p>

# Quickey

Drag-and-key file conversion for Windows. Start dragging files in File Explorer, tap a key, and drop
them onto a wheel of options. Everything runs on your PC; nothing is uploaded.

| Key (default) | Wheel   | What's on it |
|---------------|---------|--------------|
| **Shift**     | Convert | Images ↔ PNG, JPG, WebP, AVIF, GIF, BMP, TIFF, ICO, PDF · PDF → PNG, JPG, text, Word · Word/Excel/PowerPoint → PDF, DOCX, XLSX, CSV, PPTX, PNG slides… |
| **Ctrl**      | Tools   | Resize 50%, fit 1920, compress, rotate, trim, square crop, strip metadata, combine images into PDF · PDF merge, split, rotate, compress |
| **Alt**       | Actions | Copy path, copy name, copy image, copy to…, move to…, open terminal here, share |

Tap another key mid-drag to switch wheels. **Esc** or dropping in the middle cancels. Keys are changeable
in Settings. You can also right-click files → **Convert with Quickey** (Windows 11: *Show more options*).

Excel → CSV and Excel → Word (a table per sheet) read the workbook directly and work without Office.
Excel workbooks with several sheets ask which to convert (the current sheet is the default, or pick
all / specific sheets); "Don't ask again" and Settings make it automatic.

Results are saved next to the original (or in a `Quickey` subfolder) and **never overwrite** anything:
`photo.png` → `photo.webp`, then `photo (1).webp`, …

## Free vs Pro

- **Free:** 10 conversions/tools per day (each file counts once). File actions are always free and unlimited.
- **Pro:** unlimited. Sold through Lemon Squeezy; the key is activated in Settings → License.

## Project layout

```
src/Quickey.Core        Cross-platform engine: operations, image (Magick.NET) and PDF (PDFsharp,
                        PdfPig, PDFtoImage) conversions, licensing, settings. Fully unit-tested.
src/Quickey.App         Windows app (WPF, .NET 8): drag hooks, radial wheel, tray, settings,
                        Office conversions via COM, Explorer menu, Velopack updates.
tests/Quickey.Core.Tests
.github/workflows       Windows build → installer (Setup.exe) on every push; GitHub Release on v* tags.
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the drag-and-key wheel works and
[docs/LAUNCH.md](docs/LAUNCH.md) for the steps to start selling.

## Build

Requires the .NET 8 SDK.

```powershell
dotnet test tests/Quickey.Core.Tests          # engine tests (also run on Linux/macOS)
dotnet run --project src/Quickey.App          # Windows only
```

Installer: push to `main` and download the `Quickey-<version>` artifact from the Actions run, or tag
`v0.1.0` to publish a GitHub Release. It contains `Quickey-win-Setup.exe` (per-user install, no admin).

## Requirements

Windows 10 2004 (build 19041) or later, x64. Office document conversions need Microsoft Office
installed; without it those options show greyed out with "Needs Microsoft Word" etc.

## Third-party licenses

All dependencies are permissively licensed (MIT / Apache-2.0). **PDFsharp is pinned to 6.2.4**, the last
MIT release; 6.5 and later are AGPL-3.0 and must not be used in this closed-source app.
