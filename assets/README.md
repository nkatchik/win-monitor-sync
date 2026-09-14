`monitor-sync.svg` is the source artwork for both Windows icons. To regenerate
them on Windows with the .NET 10 SDK, run from the repository root:

```powershell
dotnet run --project scripts/GenerateIcons -- assets
```

Commit the generated ICO files alongside SVG changes. Normal builds use these
files directly and do not require the generator.

Each size is rendered at eight times its resolution, then reduced by averaging
premultiplied colour and alpha coverage. This smooths curves and diagonal edges
without adding fringes around the rounded background.

Both icons contain images at 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 pixels,
with transparency outside the rounded square. The executable and tray use
`monitor-sync.ico`: a white sun and three sound waves on a solid blue background.
The first wave shares the sun's centre, radius and stroke thickness.

`monitor-sync-tray.ico` is a black-background, white-glyph coverage map used only
in high contrast. The tray maps it to the system window background and text
colours, preserving antialiasing. Tray size follows taskbar DPI, and high-contrast
changes apply while the app is running.
If the taskbar is temporarily unavailable, the tray keeps its last known pixel
size; the first render uses system DPI as its fallback rather than assuming 96 DPI.
