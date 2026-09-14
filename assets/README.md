`monitor-sync.svg` is the source artwork for both Windows icons. To regenerate
them on Windows with the .NET 10 SDK, run from the repository root:

```powershell
dotnet run --project scripts/GenerateIcons -- assets
```

Commit the generated ICO files alongside SVG changes. Normal builds use these
files directly and do not require the generator.

Both icons contain transparent images at 16, 20, 24, 32, 40, 48, 64, 96, 128 and
256 pixels. The executable uses the neutral grey `monitor-sync.ico`. The tray
uses `monitor-sync-tray.ico` as an alpha mask, choosing its size and ink from the
taskbar DPI and system theme, including high contrast and live theme changes.
