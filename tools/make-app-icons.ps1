# Draws the app icons that can be chosen in Settings > Appearance > App icon, as src\IGYO\Assets\AppIcons\icons.json
# lists them: the 異 seal (tools\make-icon.ps1 -Shape seal), its kanji in "star" (the list's name for the mark's color)
# on a stamp of "tile", running down to "tile2" when there is one (through "tileMid" halfway when there is that too),
# or through "stops" from the top down instead ("0:#976FA3,0.3:#CB73A3,…", see make-icon.ps1 -Stops). For each:
# <id>.ico (16-256 px, what the windows, the taskbar and the shortcuts show) and <id>.png (128 px, the picture
# Settings shows).
# The default one ("default" in the list, Ink: white on black) is also the program's own icon: Assets\app.ico and
# Assets\logo.png. After a change to the default: src\Setup\Setup.cs draws the same icon in the setup's header (its
# Stamp and Kanji colors and its Mark): change them together.
param([switch]$SkipDefault)
$ErrorActionPreference = 'Stop'
$assets = Join-Path (Split-Path $PSScriptRoot) 'src\IGYO\Assets'
$dir = Join-Path $assets 'AppIcons'
$list = Get-Content (Join-Path $dir 'icons.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$icons = @($list.icons)
if (-not ($icons | Where-Object { $_.id -eq $list.default })) { throw "the default icon '$($list.default)' is not in the list" }
foreach ($icon in $icons) {
  if ($icon.id -notmatch '^[a-z0-9-]+$') { throw "an icon's id is letters, digits and dashes only: '$($icon.id)'" }
  $draw = @{ OutFile = (Join-Path $dir "$($icon.id).ico"); PngOut = (Join-Path $dir "$($icon.id).png"); PngSize = 128; Shape = 'seal'; Tile = $icon.tile; Ink = $icon.star }
  if ($icon.tile2) { $draw.Tile2 = $icon.tile2 }
  if ($icon.tileMid) { $draw.TileMid = $icon.tileMid }
  if ($icon.stops) { $draw.Stops = $icon.stops }
  & (Join-Path $PSScriptRoot 'make-icon.ps1') @draw
}
if (-not $SkipDefault) {
  $icon = $icons | Where-Object { $_.id -eq $list.default }
  $draw = @{ OutFile = (Join-Path $assets 'app.ico'); PngOut = (Join-Path $assets 'logo.png'); Shape = 'seal'; Tile = $icon.tile; Ink = $icon.star }
  if ($icon.tile2) { $draw.Tile2 = $icon.tile2 }
  if ($icon.tileMid) { $draw.TileMid = $icon.tileMid }
  if ($icon.stops) { $draw.Stops = $icon.stops }
  & (Join-Path $PSScriptRoot 'make-icon.ps1') @draw
}
