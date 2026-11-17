# Builds IGYO (Release) and packs it with its engine into dist\IGYOSetup.exe.
#
#   -KeepWidevine   keeps Google's Widevine module in the packed engine and writes dist\IGYOSetup-with-Widevine.exe
#                   instead. That module (runtime\<version>\WidevineCdm) lets sites play video protected with
#                   Widevine. Its own licence file says it may not be distributed without an agreement with
#                   Google, so the setup that is published leaves it out; keep it only in a build for yourself.
param([switch]$KeepWidevine)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'out'

$engine = Get-ChildItem (Join-Path $root 'runtime') -Directory -ErrorAction SilentlyContinue |
  Where-Object { Test-Path (Join-Path $_.FullName 'msedgewebview2.exe') } | Select-Object -First 1
if (-not $engine) { throw "The engine is missing: expected runtime\<version>\msedgewebview2.exe (Microsoft's fixed-version WebView2 Runtime)." }

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
dotnet build (Join-Path $root 'src\IGYO\IGYO.csproj') -c Release -o (Join-Path $out 'app') --nologo -v minimal
if ($LASTEXITCODE) { throw 'The browser did not build.' }

$stage = Join-Path $out 'stage'
New-Item -ItemType Directory -Force $stage | Out-Null
Get-ChildItem (Join-Path $out 'app') | Where-Object { $_.Extension -notin '.pdb', '.xml' } | Copy-Item -Destination $stage -Recurse
$staged = Join-Path $stage "runtime\$($engine.Name)"
Copy-Item $engine.FullName $staged -Recurse
if (-not $KeepWidevine) { Remove-Item (Join-Path $staged 'WidevineCdm') -Recurse -Force -ErrorAction SilentlyContinue }

# Every file in the package gets the same date. A package keeps each file's date as the clock of the PC that
# built it showed it, so the real ones would tell when, and in which time zone, the build was made.
$stamp = [DateTime]::new(2026, 1, 1, 0, 0, 0)
Get-ChildItem $stage -Recurse -Force | ForEach-Object { $_.LastWriteTime = $stamp }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $out 'payload.zip'
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

dotnet build (Join-Path $root 'src\Setup\Setup.csproj') -c Release -o (Join-Path $out 'setup') --nologo -v minimal "-p:PayloadZip=$zip"
if ($LASTEXITCODE) { throw 'Setup did not build.' }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
# Setups under the browser's earlier names (No Suggestions Browser, then Tacit), so that only the new one is left to run.
'NoSuggestionsBrowserSetup.exe', 'TacitSetup.exe', 'TacitSetup-with-Widevine.exe' |
  ForEach-Object { Remove-Item (Join-Path $dist $_) -ErrorAction SilentlyContinue }
$name = if ($KeepWidevine) { 'IGYOSetup-with-Widevine.exe' } else { 'IGYOSetup.exe' }
Copy-Item (Join-Path $out 'setup\IGYOSetup.exe') (Join-Path $dist $name) -Force
Remove-Item $stage -Recurse -Force
Remove-Item (Join-Path $out 'payload.zip'), (Join-Path $out 'setup') -Recurse -Force -ErrorAction SilentlyContinue
$setup = Get-Item (Join-Path $dist $name)
"Built {0} ({1:N0} MB) with engine {2}, {3}" -f $setup.FullName, ($setup.Length / 1MB), $engine.Name, $(if ($KeepWidevine) { 'Widevine module included: not for publishing' } else { 'without the Widevine module' })
