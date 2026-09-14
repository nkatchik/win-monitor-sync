`monitor-sync.svg` is the source artwork for both Windows icons. To regenerate
them on Windows with the .NET 10 SDK, run from the repository root:

```powershell
dotnet run --project scripts/GenerateIcons -- assets
```

Commit the generated ICO files alongside SVG changes. Normal builds use these
files directly and do not require the generator.

Both icons contain images at 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 pixels,
with transparency outside the rounded square. The executable and tray use
`monitor-sync.ico`: a white sun and three sound waves on a solid blue background.
The first wave shares the sun's centre, radius and stroke thickness.

`monitor-sync-tray.ico` is a black-background, white-glyph coverage map used only
in high contrast. The tray maps it to the system window background and text
colours, preserving antialiasing. Tray size follows taskbar DPI, and high-contrast
changes apply while the app is running.
