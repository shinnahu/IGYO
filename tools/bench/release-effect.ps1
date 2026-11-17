# Shows what happens in Windows as a whole when a browser's memory is taken out of its working sets, the way
# IGYO's "Give memory back to Windows" does it (EmptyWorkingSet on each of its processes).
# Starts a copy of IGYO with that setting, sleep and unload all switched off, opens ten local pages, lets them
# settle, then empties the working set of every process of that copy from here, and follows for some minutes:
#   - the copy's own memory in use (private working sets) and committed memory (private bytes)
#   - Windows' own counters: available memory, the modified page list (memory that has to be saved to the paging
#     file before anything else can use it), the standby list, free memory, the compression store, and how much
#     was written to and read from the paging file
# Other programs on the PC move the same counters, so only large moves right at the moment of the release can be
# put down to it. Needs Node.js for the page server. It is how the "What letting go does" table in
# docs\performance.md was made.
#
#   powershell -ExecutionPolicy Bypass -File release-effect.ps1 -IGYO "<folder>\IGYO.exe" [-Work <folder>]
#
# IGYO is given a folder inside the work folder as its data folder (NSB_DATA_DIR), so the profiles and bookmarks
# of an installed IGYO are not touched. Result: <Work>\release-effect.json, and a table on the screen.
param([Parameter(Mandatory = $true)][string]$IGYO, [string]$Work = (Join-Path $env:TEMP 'igyo-bench'),
      [int]$Tabs = 10, [int]$Settle = 60, [int]$Follow = 300)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
New-Item -ItemType Directory -Force $Work | Out-Null
$data = Join-Path $Work 'data-release-effect'
$out = Join-Path $Work 'release-effect.json'
$igyoExe = (Resolve-Path $IGYO).Path

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class WorkingSets {
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);
    public static bool Empty(int pid) {
        IntPtr h = OpenProcess(0x1000 | 0x0100, false, pid);   // query limited information + set quota, as IGYO asks
        if (h == IntPtr.Zero) return false;
        try { return EmptyWorkingSet(h); } finally { CloseHandle(h); }
    }
}
"@

function TreePids([int]$root) {
  $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CreationDate, Name)
  $rootProc = $all | Where-Object { $_.ProcessId -eq $root } | Select-Object -First 1
  if (-not $rootProc) { return @() }
  $list = New-Object System.Collections.Generic.List[int]; $queue = New-Object System.Collections.Queue; $queue.Enqueue($rootProc)
  while ($queue.Count) {
    $p = $queue.Dequeue(); $list.Add([int]$p.ProcessId)
    foreach ($c in $all) { if ($c.ParentProcessId -eq $p.ProcessId -and $c.ProcessId -ne $p.ProcessId -and $c.CreationDate -ge $p.CreationDate) { $queue.Enqueue($c) } }
  }
  $list.ToArray()
}
function Reading($pids, $seconds) {
  $perf = @(Get-CimInstance Win32_PerfRawData_PerfProc_Process -Property IDProcess, Name, WorkingSetPrivate, WorkingSet, PrivateBytes)
  $mine = @($perf | Where-Object { $pids -contains [int]$_.IDProcess })
  $store = $perf | Where-Object { $_.Name -eq 'Memory Compression' } | Select-Object -First 1
  $m = Get-CimInstance Win32_PerfRawData_PerfOS_Memory
  [pscustomobject]@{
    seconds = $seconds
    browserInUseMB = [Math]::Round((($mine | Measure-Object WorkingSetPrivate -Sum).Sum) / 1MB, 1)
    browserCommitMB = [Math]::Round((($mine | Measure-Object PrivateBytes -Sum).Sum) / 1MB, 1)
    availableMB = [Math]::Round($m.AvailableBytes / 1MB)
    modifiedMB = [Math]::Round($m.ModifiedPageListBytes / 1MB)
    standbyMB = [Math]::Round(($m.StandbyCacheCoreBytes + $m.StandbyCacheNormalPriorityBytes + $m.StandbyCacheReserveBytes) / 1MB)
    freeMB = [Math]::Round($m.FreeAndZeroPageListBytes / 1MB)
    compressionStoreMB = $(if ($store) { [Math]::Round($store.WorkingSet / 1MB) } else { $null })
    pagesWritten = [int64]$m.PagesOutputPersec   # raw counters: pages written to and read from disk since Windows started
    pagesRead = [int64]$m.PagesInputPersec
  }
}

if (Test-Path $data) { Remove-Item -LiteralPath $data -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $data 'Profiles\Default\NewTab') | Out-Null
'{"FirstRunDone":true,"SearchSuggestions":false,"RestoreTabs":false,"ReleaseMemory":false,"SleepAfterMinutes":0,"UnloadAfterMinutes":0}' | Set-Content (Join-Path $data 'settings.json') -Encoding ascii
'{"Shortcuts":[{"Name":"Local","Url":"http://s1.localhost:18811/p/1"}]}' | Set-Content (Join-Path $data 'Profiles\Default\NewTab\newtab.json') -Encoding ascii

$server = Start-Process -FilePath node -ArgumentList "`"$here\server.js`"" -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 1
$root = 0
try {
  $env:NSB_DATA_DIR = $data
  Remove-Item Env:\WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS, Env:\NSB_RUNTIME_DIR, Env:\NSB_SUGGEST_URL, Env:\NSB_NO_ACTIVATE, Env:\NSB_STARTUP_TRACE -ErrorAction SilentlyContinue
  $urls = (1..$Tabs | ForEach-Object { "`"http://s$_.localhost:18811/p/$($_)?run=release-effect`"" }) -join ' '
  $root = [int](& (Join-Path $here 'hidden-desktop.ps1') -Exe $igyoExe -Arguments $urls -Desktop 'IGYOBench-release')
  "started with $Tabs pages; letting them settle for $Settle s"
  Start-Sleep -Seconds ([Math]::Max(1, $Settle - 20))
  $pids = @(TreePids $root)
  $rows = New-Object System.Collections.Generic.List[object]
  for ($t = -20; $t -lt 0; $t += 4) { $rows.Add((Reading $pids $t)); Start-Sleep -Seconds 4 }
  $pids = @(TreePids $root)
  $before = Reading $pids 0; $rows.Add($before)
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $emptied = 0; foreach ($id in $pids) { if ([WorkingSets]::Empty($id)) { $emptied++ } }
  $took = $sw.ElapsedMilliseconds
  "emptied the working set of $emptied of $($pids.Count) processes in $took ms"
  foreach ($mark in @(1, 3, 6, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 300 | Where-Object { $_ -le $Follow })) {
    $wait = $mark - $sw.Elapsed.TotalSeconds; if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }
    $rows.Add((Reading $pids $mark))
  }
  $table = $rows | Select-Object seconds, browserInUseMB, browserCommitMB, availableMB, modifiedMB, standbyMB, freeMB, compressionStoreMB,
    @{ n = 'writtenToDiskMB'; e = { [Math]::Round(($_.pagesWritten - $before.pagesWritten) * 4096 / 1MB) } }, @{ n = 'readFromDiskMB'; e = { [Math]::Round(($_.pagesRead - $before.pagesRead) * 4096 / 1MB) } }
  $table | Format-Table -AutoSize | Out-String -Width 250
  [ordered]@{ when = [DateTime]::UtcNow.ToString('yyyy-MM-dd'); igyo = (Get-Item $igyoExe).VersionInfo.ProductVersion; tabs = $Tabs; settleSeconds = $Settle; processes = $pids.Count; emptied = $emptied; tookMs = $took; rows = @($table) } |
    ConvertTo-Json -Depth 5 | Set-Content $out -Encoding utf8
}
finally {
  if ($root) { foreach ($id in @(TreePids $root | Sort-Object -Descending)) { try { Stop-Process -Id $id -Force -ErrorAction Stop } catch { } } }
  try { if (-not $server.HasExited) { $server.Kill() } } catch { }
}
