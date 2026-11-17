# Measures an installed (Release) copy of IGYO and Google Chrome with the same local pages, each on a throwaway
# profile, each on a Windows desktop of its own that is never shown. It is how the numbers in docs\performance.md
# were made.
#   start-up   time from starting the program to a local page reporting that it has loaded (several runs)
#   one tab    memory with one page open, at several moments
#   ten tabs   memory with ten pages open, some minutes apart (the browsers left alone with their own defaults)
#
# IGYO hands memory back to Windows differently when you are using it and when you are not, so it is measured
# in each situation, and once with that setting switched off:
#   igyo        its window is the active one, as while you use it
#   igyo-away   its window was opened without becoming the active one, as while you use another program
#   igyo-keep   as "igyo", with Settings > Memory saver > "Give memory back to Windows" switched off
#   chrome       Google Chrome
# All of them run at the same time, each with its own data folder and its own desktop.
#
# Memory is the sum, over every process of the browser, of its private working set (the memory the process has to
# itself that is in RAM at that moment). Each reading also keeps that sum, and the processor time used so far, by
# kind of process. Chrome is started
# with its background networking and Cast discovery off and with every name lookup except localhost failing, so
# its pages and its own background services reach nothing outside the PC. Chrome also starts its own updater, a
# separate program these switches do not reach; when it is running at a reading, the log's list of processes shows
# it (updater.exe). Needs Node.js for the page server.
#
#   powershell -ExecutionPolicy Bypass -File bench.ps1 -IGYO "<folder>\IGYO.exe" [-Work <folder for profiles and logs>]
#   -StartupOnly   only the start-up runs
#   -NoChrome      IGYO only
#   -Quick         one run of each, short waits (to try the script out)
#
# IGYO is given folders inside the work folder as its data folders (NSB_DATA_DIR), so the profiles and bookmarks
# of an installed IGYO are not touched. Results: <Work>\results.json and <Work>\bench.log.
param([Parameter(Mandatory = $true)][string]$IGYO, [string]$Chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe',
      [string]$Work = (Join-Path $env:TEMP 'igyo-bench'), [int]$Runs = 5, [int[]]$OneTabSeconds = @(45, 120, 300),
      [int[]]$SampleMinutes = @(1, 2, 4, 7, 16, 32), [switch]$Quick, [switch]$StartupOnly, [switch]$NoChrome)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
New-Item -ItemType Directory -Force $Work | Out-Null
$log = Join-Path $Work 'bench.log'
$doneLog = Join-Path $here 'done.log'
$tenSeconds = @($SampleMinutes | ForEach-Object { $_ * 60 })
$apart = 20   # seconds between one browser's start and the next, so that their readings never fall on each other
if ($Quick) { $Runs = 1; $OneTabSeconds = @(15); $tenSeconds = @(20); $apart = 14 }
function Say($text) { $line = "[{0}] {1}" -f [DateTime]::UtcNow.ToString('HH:mm:ss'), $text; Add-Content $log $line; $line }
function NowMs { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
function Median($values) { $s = @($values | Sort-Object); if (-not $s.Count) { return $null }; if ($s.Count % 2) { $s[($s.Count - 1) / 2] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 } }

$igyoExe = (Resolve-Path $IGYO).Path
$chromeExe = $Chrome
if (-not $NoChrome -and -not (Test-Path $chromeExe)) { throw "Chrome was not found: $chromeExe (or use -NoChrome)" }
$chromeFlags = '--user-data-dir="{0}" --no-first-run --no-default-browser-check --disable-background-networking --disable-component-update --disable-sync --disable-domain-reliability --no-pings --disable-session-crashed-bubble --hide-crash-restore-bubble --disable-features=DnsOverHttpsUpgrade,MediaRouter,DialMediaRouteProvider,CastMediaRouteProvider --host-resolver-rules="MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE *.localhost, EXCLUDE 127.0.0.1"'
$igyoSettings = '"FirstRunDone":true,"SearchSuggestions":false,"RestoreTabs":false'
$subjects = [ordered]@{
  'igyo'      = @{ igyo = $true; away = $false; settings = "{$igyoSettings}" }
  'igyo-away' = @{ igyo = $true; away = $true; settings = "{$igyoSettings}" }
  'igyo-keep' = @{ igyo = $true; away = $false; settings = "{$igyoSettings,`"ReleaseMemory`":false}" }
  'chrome'     = @{ igyo = $false }
}
if ($NoChrome) { $subjects.Remove('chrome') }
foreach ($name in @($subjects.Keys)) { $subjects[$name].data = Join-Path $Work "data-$name"; $subjects[$name].desktop = "IGYOBench-$name" }
$timed = @('igyo', 'chrome' | Where-Object { $subjects.Contains($_) })   # the two whose start-up is timed

# ----- processes -----
function Procs([switch]$Commands) {
  $props = @('ProcessId', 'ParentProcessId', 'CreationDate', 'Name'); if ($Commands) { $props += 'CommandLine' }
  @(Get-CimInstance Win32_Process -Property $props)
}
function TreeOf($all, [int]$root) {
  $rootProc = $all | Where-Object { $_.ProcessId -eq $root } | Select-Object -First 1
  if (-not $rootProc) { return @() }
  $list = New-Object System.Collections.Generic.List[object]
  $queue = New-Object System.Collections.Queue
  $queue.Enqueue($rootProc)
  while ($queue.Count) {
    $p = $queue.Dequeue(); $list.Add($p)
    foreach ($c in $all) { if ($c.ParentProcessId -eq $p.ProcessId -and $c.ProcessId -ne $p.ProcessId -and $c.CreationDate -ge $p.CreationDate) { $queue.Enqueue($c) } }
  }
  $list.ToArray()
}
# What a process is for, from the switches the browser started it with: main (none), renderer (pages),
# gpu-process, utility:NetworkService and so on.
function Kind($process) {
  $c = [string]$process.CommandLine
  if ($c -notmatch '--type=([a-z-]+)') { return 'main' }
  $kind = $Matches[1]
  if ($kind -eq 'utility' -and $c -match '--utility-sub-type=([A-Za-z_.]+)') { $kind = 'utility:' + ($Matches[1] -replace '^.*\.', '') }
  $kind
}
function TreeUse([int]$root) {
  $tree = @(TreeOf (Procs -Commands) $root)
  $pids = @($tree | ForEach-Object { [int]$_.ProcessId })
  if (-not $pids.Count) { return $null }
  $perf = @{}
  Get-CimInstance Win32_PerfRawData_PerfProc_Process -Property IDProcess, WorkingSetPrivate, WorkingSet, PrivateBytes | Where-Object { $pids -contains [int]$_.IDProcess } | ForEach-Object { $perf[[int]$_.IDProcess] = $_ }
  $cpu = 0.0; $cpuOf = @{}
  foreach ($id in $pids) { try { $cpuOf[$id] = (Get-Process -Id $id -ErrorAction Stop).TotalProcessorTime.TotalSeconds; $cpu += $cpuOf[$id] } catch { } }
  $rows = @(foreach ($p in $tree) { $m = $perf[[int]$p.ProcessId]; if ($m) { [pscustomobject]@{ name = $p.Name; kind = (Kind $p); bytes = [double]$m.WorkingSetPrivate; cpu = [double]$cpuOf[[int]$p.ProcessId] } } })
  $kinds = @($rows | Group-Object name, kind | ForEach-Object { [pscustomobject]@{ name = $_.Group[0].name; kind = $_.Group[0].kind; count = $_.Count; mb = [Math]::Round((($_.Group | Measure-Object bytes -Sum).Sum) / 1MB, 1); cpuSeconds = [Math]::Round((($_.Group | Measure-Object cpu -Sum).Sum), 2) } })
  [pscustomobject]@{
    processes = $pids.Count
    privateWorkingSetMB = [Math]::Round((($perf.Values | Measure-Object WorkingSetPrivate -Sum).Sum) / 1MB, 1)
    workingSetMB = [Math]::Round((($perf.Values | Measure-Object WorkingSet -Sum).Sum) / 1MB, 1)
    commitMB = [Math]::Round((($perf.Values | Measure-Object PrivateBytes -Sum).Sum) / 1MB, 1)
    cpuSeconds = [Math]::Round($cpu, 2)
    kinds = $kinds
    names = (($tree | Group-Object Name | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', ')
    pids = $pids
  }
}
# Three readings five seconds apart; the middle one by private working set is kept.
function Sample([int]$root) {
  $readings = @()
  for ($i = 0; $i -lt 3; $i++) { $m = TreeUse $root; if ($m) { $readings += $m }; if ($i -lt 2) { Start-Sleep -Seconds 5 } }
  if (-not $readings.Count) { return $null }
  @($readings | Sort-Object privateWorkingSetMB)[[Math]::Floor($readings.Count / 2)]
}
function StopTree([int]$root) {
  $tree = @(TreeOf (Procs) $root)
  foreach ($p in @($tree | Sort-Object CreationDate -Descending)) { try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop } catch { } }
  Start-Sleep -Seconds 2
}
$roots = @{}; $loaded = @{}; $current = 0
$seen = @{}; foreach ($name in $subjects.Keys) { $seen[$name] = New-Object System.Collections.Generic.HashSet[string] }
# Connections to other computers that the running browsers have open at this moment.
function NoteConnections {
  if (-not $roots.Count) { return }
  $all = Procs
  $owner = @{}
  foreach ($name in @($roots.Keys)) { foreach ($p in @(TreeOf $all $roots[$name])) { $owner[[int]$p.ProcessId] = $name } }
  if (-not $owner.Count) { return }
  try {
    foreach ($c in Get-NetTCPConnection -ErrorAction Stop | Where-Object { $owner.ContainsKey([int]$_.OwningProcess) -and $_.State -ne 'Listen' -and $_.RemoteAddress -notin '127.0.0.1', '::1', '0.0.0.0', '::' }) {
      [void]$seen[$owner[[int]$c.OwningProcess]].Add("$($c.RemoteAddress):$($c.RemotePort)")
    }
  } catch { }
}
function WaitUntil($due) {
  while ((Get-Date) -lt $due) {
    NoteConnections
    $left = ($due - (Get-Date)).TotalSeconds
    if ($left -gt 0) { Start-Sleep -Milliseconds ([int]([Math]::Min(10, $left) * 1000)) }
  }
}

# ----- starting things -----
function Beacons($run) {
  if (-not (Test-Path $doneLog)) { return @() }
  @(Get-Content $doneLog | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.run -eq $run })
}
function WaitBeacons($run, $count, $seconds) {
  $end = (Get-Date).AddSeconds($seconds)
  do { $got = @(Beacons $run); if ($got.Count -ge $count) { return $got }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $end)
  Beacons $run
}
function Launch($name, [string[]]$urls) {
  $subject = $subjects[$name]
  Remove-Item Env:\WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS, Env:\NSB_RUNTIME_DIR, Env:\NSB_SUGGEST_URL, Env:\NSB_BRIDGE_ASK_SECONDS, Env:\NSB_BRIDGE_BAR_SECONDS, Env:\NSB_STARTUP_TRACE, Env:\NSB_NO_ACTIVATE -ErrorAction SilentlyContinue
  if ($subject.igyo) {
    $env:NSB_DATA_DIR = $subject.data
    if ($subject.away) { $env:NSB_NO_ACTIVATE = '1' }
    $exe = $igyoExe; $flags = ''
  } else { $exe = $chromeExe; $flags = $chromeFlags -f $subject.data }
  $arguments = (@($flags) + ($urls | ForEach-Object { "`"$_`"" }) | Where-Object { $_ }) -join ' '
  [int](& (Join-Path $here 'hidden-desktop.ps1') -Exe $exe -Arguments $arguments -Desktop $subject.desktop)
}
function Urls($run, $count) { 1..$count | ForEach-Object { "http://s$_.localhost:18811/p/$($_)?run=$run" } }

# Starts every browser with $count pages, one after another and a little apart, and reads each one's memory at
# the given seconds after its own last page reported that it had loaded. Whatever is due first is done first
# (the next start, or a reading), so a reading is late only by what was being done when it fell due; each one
# keeps the second it was really begun at.
function Phase($label, $count, $seconds, $store) {
  $names = @($subjects.Keys)
  $next = 0
  $startAt = Get-Date
  $due = New-Object System.Collections.Generic.List[object]
  while ($next -lt $names.Count -or $due.Count) {
    $first = -1
    for ($k = 0; $k -lt $due.Count; $k++) { if ($first -lt 0 -or $due[$k].at -lt $due[$first].at) { $first = $k } }
    $reading = $(if ($first -ge 0) { $due[$first] } else { $null })
    if ($next -lt $names.Count -and (-not $reading -or $startAt -le $reading.at)) {
      WaitUntil $startAt
      $name = $names[$next]; $next++
      $run = "$label-$name"
      $roots[$name] = Launch $name (Urls $run $count)
      $got = @(WaitBeacons $run $count 120)
      $loaded[$name] = Get-Date
      Say ("  {0}: {1} of {2} pages reported" -f $name, $got.Count, $count)
      foreach ($s in $seconds) { $due.Add([pscustomobject]@{ at = $loaded[$name].AddSeconds($s); name = $name; seconds = $s }) }
      $startAt = $loaded[$name].AddSeconds($apart)
      continue
    }
    WaitUntil $reading.at
    $due.RemoveAt($first)
    $name = $reading.name; $s = $reading.seconds
    $begun = [int][Math]::Round(((Get-Date) - $loaded[$name]).TotalSeconds)
    $m = Sample $roots[$name]
    if (-not $m) { Say "  $name at $s s: it is not running any more"; continue }
    $store[$name] += ($m | Select-Object @{ n = 'seconds'; e = { $s } }, @{ n = 'begunAtSeconds'; e = { $begun } }, processes, privateWorkingSetMB, workingSetMB, commitMB, cpuSeconds, kinds, pids)
    Say ("  {0} at {1} s (begun at {2} s): {3} MB private working set, {4} MB commit, {5} s of processor time so far, {6} processes ({7})" -f $name, $s, $begun, $m.privateWorkingSetMB, $m.commitMB, $m.cpuSeconds, $m.processes, $m.names)
    Say ("      " + (($m.kinds | Sort-Object mb -Descending | ForEach-Object { "$($_.name) $($_.kind) x$($_.count): $($_.mb) MB" }) -join '; '))
  }
  NoteConnections
  foreach ($name in @($roots.Keys)) { StopTree $roots[$name] }
  $roots.Clear()
}

Remove-Item $log, $doneLog -ErrorAction SilentlyContinue
foreach ($name in $subjects.Keys) {
  $subject = $subjects[$name]
  if (Test-Path $subject.data) { Remove-Item -LiteralPath $subject.data -Recurse -Force }
  if (-not $subject.igyo) { continue }
  New-Item -ItemType Directory -Force (Join-Path $subject.data 'Profiles\Default\NewTab') | Out-Null
  $subject.settings | Set-Content (Join-Path $subject.data 'settings.json') -Encoding ascii
  '{"Shortcuts":[{"Name":"Local","Url":"http://s1.localhost:18811/p/1"}]}' | Set-Content (Join-Path $subject.data 'Profiles\Default\NewTab\newtab.json') -Encoding ascii
}

$server = Start-Process -FilePath node -ArgumentList "`"$here\server.js`"" -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 1
& (Join-Path $here 'hidden-desktop.ps1') -Exe "$env:SystemRoot\System32\cmd.exe" -Arguments '/c exit' | Out-Null   # compiles the launcher before anything is timed

$os = Get-CimInstance Win32_OperatingSystem
$result = [ordered]@{
  when = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
  igyo = (Get-Item $igyoExe).VersionInfo.ProductVersion
  engine = (Get-ChildItem (Join-Path (Split-Path $igyoExe -Parent) 'runtime') -Directory -ErrorAction SilentlyContinue | Select-Object -First 1).Name
  chrome = $(if ($NoChrome) { '' } else { (Get-Item $chromeExe).VersionInfo.ProductVersion })
  memoryGB = [Math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
  freeMemoryGB = [Math]::Round($os.FreePhysicalMemory / 1MB, 1)
  logicalProcessors = [Environment]::ProcessorCount
  startupMs = [ordered]@{}
  pageLoadMs = [ordered]@{}
  oneTab = [ordered]@{}
  tenTabs = [ordered]@{}
  connections = [ordered]@{}
}
foreach ($name in $timed) { $result.startupMs[$name] = @(); $result.pageLoadMs[$name] = @() }
foreach ($name in $subjects.Keys) { $result.oneTab[$name] = @(); $result.tenTabs[$name] = @() }

try {
  # ----- start-up: run 0 makes the profile and is not counted -----
  Say "start-up, $Runs counted runs each"
  for ($i = 0; $i -le $Runs; $i++) {
    foreach ($name in $timed) {
      $run = "st-$name-$i"
      $t0 = NowMs
      $root = Launch $name (Urls $run 1); $current = $root
      $got = @(WaitBeacons $run 1 40)
      if ($got.Count) {
        $ms = $got[0].at - $t0
        if ($i -gt 0) { $result.startupMs[$name] += [int]$ms; $result.pageLoadMs[$name] += [int]$got[0].ms }
        Say ("  {0} run {1}: {2} ms from start to the page's load event (the page itself took {3} ms){4}" -f $name, $i, $ms, $got[0].ms, $(if ($i -eq 0) { '  [not counted]' } else { '' }))
      } else { Say "  $name run ${i}: the page never reported" }
      Start-Sleep -Seconds 2
      StopTree $root
    }
  }
  $result.startupMedianMs = [ordered]@{}
  foreach ($name in $timed) { $result.startupMedianMs[$name] = (Median $result.startupMs[$name]) }
  Say ("start-up medians: " + (($timed | ForEach-Object { "$_ $($result.startupMedianMs[$_]) ms" }) -join ', '))
  if ($StartupOnly) { $result | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Work 'results-startup.json') -Encoding utf8; return }

  # The other copies of IGYO get one start that is not measured too, so that none is read on its very first start.
  foreach ($name in @($subjects.Keys | Where-Object { $timed -notcontains $_ })) {
    $run = "warm-$name"
    $root = Launch $name (Urls $run 1); $current = $root
    [void](WaitBeacons $run 1 40)
    Start-Sleep -Seconds 2
    StopTree $root
  }
  $current = 0

  Say "one tab; readings $($OneTabSeconds -join ', ') s after the page loaded"
  Phase 'one' 1 $OneTabSeconds $result.oneTab
  Say "ten tabs; readings $($tenSeconds -join ', ') s after the last page loaded"
  Phase 'ten' 10 $tenSeconds $result.tenTabs

  foreach ($name in $subjects.Keys) { $result.connections[$name] = @($seen[$name] | Sort-Object) }
  Say ("connections to other computers seen (looked at about every 10 s): " + (($subjects.Keys | ForEach-Object { "$_ [" + (($seen[$_] | Sort-Object) -join ', ') + "]" }) -join '  '))
  $result | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Work 'results.json') -Encoding utf8
  Say 'done'
}
catch { Say ("FAILED: " + $_.Exception.Message + " at " + $_.InvocationInfo.PositionMessage) }
finally {
  foreach ($name in @($roots.Keys)) { try { StopTree $roots[$name] } catch { } }
  if ($current) { try { StopTree $current } catch { } }
  try { if (-not $server.HasExited) { $server.Kill() } } catch { }
}
