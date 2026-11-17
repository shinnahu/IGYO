# Memory and performance: what was measured

IGYO 1.0.1 (then called Tacit) against Google Chrome 157, with the same ten pages, on one PC. The figures were read by the scripts in
`tools\bench`, which are in this repository so the readings can be repeated; where one was read another way, it
says so.

## In short

| | IGYO, in use | IGYO, in the background | Chrome 157 |
|---|---|---|---|
| Ten tabs, memory in use after 1 minute | 304 MB | 71 MB | 401 MB |
| Ten tabs, memory in use after 2 minutes | 307 MB | 148 MB | 399 MB |
| Ten tabs, memory in use after 4 minutes | 307 MB | 270 MB | 395 MB |
| Ten tabs, memory in use after 7 minutes | 107 MB | 83 MB | 395 MB |
| Ten tabs, memory in use after 16 minutes | 112 MB | 90 MB | 392 MB |
| Ten tabs, memory in use after 32 minutes | 121 MB | 116 MB | 394 MB |
| Ten tabs, committed memory after 7 minutes | 700 MB | 702 MB | 596 MB |
| Ten tabs, committed memory after 32 minutes | 305 MB | 309 MB | 603 MB |
| One tab, memory in use after 45 seconds | 62 MB | 60 MB | 127 MB |
| One tab, memory in use after 2 minutes | 74 MB | 43 MB | 119 MB |
| One tab, memory in use after 5 minutes | 78 MB | 49 MB | 121 MB |
| One tab, committed memory after 5 minutes | 273 MB | 268 MB | 241 MB |
| From starting the program to a loaded page (median of 7) | 0.65 s | not timed | 0.47 s |
| Processor time per minute with ten tabs, minute 7 to 16 | 0.31 s | 0.21 s | 0.09 s |

"In use" in the headings means IGYO's window was the active one, as while you use it. "In the background" means it
was opened without becoming the active window, as while you use another program; IGYO then lets go of more. Chrome
was measured one way, its window the only one on its desktop.

- **Where IGYO is ahead:** memory in use, at every reading: with ten tabs (107 MB against 395 MB after
  7 minutes, 121 MB against 394 MB after 32 minutes) and with one (78 MB against 121 MB after
  5 minutes). And committed memory once background tabs have been unloaded (305 MB against 603 MB after
  32 minutes).
- **Where Chrome is ahead:** it starts faster (0.47 s against 0.65 s to a loaded page). It reserves less
  memory while tabs are loaded: its committed memory was lower after 1, 2, 4, 7 and 16 minutes (596 MB against
  700 MB after 7 minutes), and with one tab (241 MB against 273 MB). And with IGYO's "Give memory
  back to Windows" switched off, Chrome has less in use while every tab is awake: 401 MB against 469 MB one
  minute after ten tabs loaded, 127 MB against 158 MB with one tab. And it uses less processor time while
  nothing is happening: 0.09 s a minute against 0.31 s with ten tabs open, most of the difference in the
  graphics process.
- **What "memory in use" does not say.** The low figures in the first rows come from IGYO letting go of memory,
  and memory that is let go is not memory that is free. It stays reserved for IGYO, and Windows keeps it until
  something else needs it; the next section has what was measured about that. Sleeping and unloading tabs is what
  makes the lasting difference.

## What letting go does, and does not do

Windows counts, for each process, the memory it has in RAM to itself right now: its private working set, the figure
Task Manager lists per process, called "memory in use" here. "Give memory back to Windows" (on by default) empties the
working set of a process that is not using it: a background tab's after half a minute, every process of IGYO after a
minute in another program, and all of them once a few seconds after starting. The process keeps running, and each
piece of memory it touches again comes back into its working set.

To see what Windows does with what was let go, `tools\bench\release-effect.ps1` started IGYO with that setting,
sleep and unload all switched off, loaded ten pages, waited 60 seconds, emptied the working sets of its
17 processes itself (119 ms), and followed Windows' own counters:

| Seconds after | IGYO: in use | IGYO: committed | Windows: available | Windows: modified list | Windows: compression store | Written to disk since |
|---|---|---|---|---|---|---|
| 0 (just before) | 476 MB | 721 MB | 19450 MB | 38 MB | 387 MB | 0 MB |
| 1 | 24 MB | 721 MB | 19473 MB | 495 MB | 387 MB | 0 MB |
| 10 | 55 MB | 716 MB | 19487 MB | 459 MB | 387 MB | 0 MB |
| 30 | 137 MB | 713 MB | 19467 MB | 368 MB | 386 MB | 0 MB |
| 60 | 170 MB | 712 MB | 19488 MB | 335 MB | 386 MB | 0 MB |
| 120 | 216 MB | 710 MB | 19408 MB | 288 MB | 386 MB | 0 MB |
| 300 | 273 MB | 715 MB | 19444 MB | 245 MB | 385 MB | 0 MB |

- IGYO's memory in use fell by 453 MB within a second. Its committed memory stayed where it was.
- Windows' modified list grew by 457 MB at the same moment, and its available memory by 23 MB. The
  modified list is memory Windows has to save to the paging file before anything else can use it.
- In the 5 minutes that followed, 0 MB were written to disk, and the compression store went from 387 to
  385 MB.
- The ten pages, awake and with nobody using them, took memory back by themselves: 273 MB in use after
  5 minutes, from 24 MB right after the release. The ten-tab readings show the same: the copy left in the
  background let go of everything a minute after it started and had 71 MB in use at the one-minute reading,
  148 MB a minute later and 270 MB after four minutes. With its tabs asleep, IGYO's figure stayed where it was
  (107 MB after 7 minutes, 112 MB after 16).

So on this PC, which had 19.0 GB available, letting go moved memory from IGYO's figure to a list Windows
keeps, and little of it became available to other programs. Other programs on the PC move the same counters; only
the moves at the moment of the release can be put down to it. What happens on a PC that is short of memory was not
measured.

## How it was measured

- **The browsers.** IGYO 1.0.1, the release build, installed by its setup program into a test folder, with its own
  engine (WebView2 157.0.4341.57). Google Chrome 157.0.8103.78. Each on a new profile. Chrome with its own default
  settings and nothing added. IGYO with its defaults except two: Google suggestions off, and reopening last time's
  tabs off, so that each start opens only the pages it is given. Each profile was started once before it was
  measured.
- **IGYO three ways, all at the same time as Chrome.** "In use": started the ordinary way, so its window is the
  active one on its desktop. "In the background": started with `NSB_NO_ACTIVATE`, which opens the window without
  making it the active one; that is how IGYO tells that another program is being used. And, to show what the
  setting does, a third copy "in use" with "Give memory back to Windows" switched off. Each copy had its own data
  folder.
- **The PC.** One PC with Windows 11, 31.9 GB of memory and 16 logical processors. 21 GB were free when the
  readings began.
- **The pages.** Ten pages made by `tools\bench\server.js` and served from the same PC, each under its own site name
  (`s1.localhost` to `s10.localhost`): an article of text with twelve drawings, a canvas, 120,000 small objects kept by
  script, and a counter that ticks every second. They are simple pages; real sites were not measured.
- **Nothing left the PC on purpose.** Chrome was started with background networking, component updates, sync and
  Cast discovery switched off, and with every name lookup except `localhost` failing. IGYO's release build takes no
  such switches. Chrome also starts its own updater, a separate program that those switches do not reach; when it is
  among a browser's processes at a reading, the reading's list of processes in `bench.log` shows it.
- **Not on screen.** Each browser ran on a Windows desktop of its own that is never shown, so none of them was
  drawing to a screen. A page that is being drawn and scrolled touches more of its memory than these did.
- **Memory in use** is the private working set of every process of the browser, added up. Three readings five seconds
  apart; the middle one is kept. **Committed memory** is the private bytes of the same processes: what the browser has
  asked Windows for, whether or not it is in RAM right now.
- **One tab.** Each browser was started with one address. Readings 45 seconds, 2 minutes and 5 minutes after the page reported
  that it had loaded.
- **Ten tabs.** Each browser was started with the ten addresses, so all ten pages loaded; then nothing was touched.
  Readings 1, 2, 4, 7, 16 and 32 minutes after the last page reported that it had loaded. IGYO shows each tab once as it
  opens them and leaves the last one on show; which of its tabs Chrome had on show was not looked at.
- **Start-up** is the time from starting the program to the moment its first page reported its `load` event to the
  local server. One run that is not counted created the profile; then 7 runs each, taking turns, with the
  browser's processes ended between runs.
- **Processor time** is what all the browser's processes used between two readings, per minute. It is only worked
  out where the same processes were running at both readings.

## Every figure

### Ten tabs

Memory in use / committed memory / number of processes.

| Minute | IGYO, in use | IGYO, in the background | IGYO, setting off | Chrome 157 | IGYO's background tabs |
|---|---|---|---|---|---|
| 1 | 304 / 712 MB / 18 | 71 / 711 MB / 17 | 469 / 714 MB / 18 | 401 / 607 MB / 17 | awake |
| 2 | 307 / 701 MB / 18 | 148 / 706 MB / 17 | 466 / 704 MB / 18 | 399 / 598 MB / 17 | awake |
| 4 | 307 / 699 MB / 18 | 270 / 701 MB / 17 | 461 / 703 MB / 18 | 395 / 596 MB / 17 | awake |
| 7 | 107 / 700 MB / 18 | 83 / 702 MB / 17 | 207 / 704 MB / 18 | 395 / 596 MB / 17 | asleep |
| 16 | 112 / 703 MB / 18 | 90 / 706 MB / 17 | 208 / 707 MB / 18 | 392 / 599 MB / 17 | asleep |
| 32 | 121 / 305 MB / 9 | 116 / 309 MB / 8 | 180 / 304 MB / 9 | 394 / 603 MB / 17 | unloaded |

IGYO's defaults put a background tab to sleep after 5 minutes and unload it after 30; the last column is what those
defaults make of the nine background tabs at that minute. The states themselves cannot be read from a release build,
so they were not read: the column follows from the settings, and the process counts agree with it.

### One tab

Memory in use / committed memory / number of processes.

| After | IGYO, in use | IGYO, in the background | IGYO, setting off | Chrome 157 |
|---|---|---|---|---|
| 45 seconds | 62 / 286 MB / 9 | 60 / 280 MB / 8 | 158 / 283 MB / 8 | 127 / 255 MB / 8 |
| 2 minutes | 74 / 271 MB / 9 | 43 / 267 MB / 8 | 150 / 268 MB / 8 | 119 / 240 MB / 8 |
| 5 minutes | 78 / 273 MB / 9 | 49 / 268 MB / 8 | 152 / 269 MB / 8 | 121 / 241 MB / 8 |

### One tab, by kind of process

Memory in use 45 seconds after loading. The middle column is the like-for-like one: nothing has been let go in it.

| | IGYO | IGYO, setting off | Chrome 157 |
|---|---|---|---|
| The program's own process (`IGYO.exe`) | 2.1 MB | 21.6 MB | none of its own |
| The engine's main process | 12.2 MB | 44.9 MB | 36.9 MB |
| Page processes | 36.4 MB in 2 (the page and IGYO's interface) | 52.7 MB in 2 (the page and IGYO's interface) | 54.9 MB in 3 |
| Graphics process | 6.0 MB | 26.3 MB | 25.5 MB |
| Network and storage processes | 1.3 MB | 10.1 MB | 7.5 MB |
| Crash-report processes | 3.7 MB | 1.9 MB | 1.5 MB |
| All | 61.7 MB | 157.6 MB | 126.5 MB |

With nothing let go, IGYO had 31.1 MB more in use than Chrome. Its own program process is 21.6 MB of that, and the
engine's main process was 8.0 MB larger than Chrome's. The page processes together were 2.2 MB smaller than Chrome's.

### Start-up

| | IGYO | Chrome 157 |
|---|---|---|
| Each run, from start to the page's load event | 662 ms, 670 ms, 645 ms, 646 ms, 647 ms, 649 ms, 678 ms | 473 ms, 473 ms, 459 ms, 459 ms, 464 ms, 466 ms, 466 ms |
| Median | 649 ms | 466 ms |
| Of that, the page's own loading (from the start of its navigation to its load event), median | 147 ms | 230 ms |

IGYO asks its engine for its first two views as its very first step, and builds its window while the engine starts.
In separate runs with the program's own trace (`NSB_STARTUP_TRACE`), about 80 ms had passed when the tab's view was
asked for, the engine handed it over about 380 to 390 ms later, and the page was loaded about 160 to 170 ms after
that. Nine changes were tried to shorten the engine's part (engine switches, and the order and number of the views
asked for); none did. IGYO 1.0.0 needed 0.94 s by the same measure as the table's.

### Processor time

Seconds of processor time per minute, all the browser's processes together, with ten tabs open and nothing happening.

| | IGYO, in use | IGYO, in the background | IGYO, setting off | Chrome 157 |
|---|---|---|---|---|
| Minute 1 to 4 (IGYO's background tabs awake) | 0.54 s | 0.37 s | 0.13 s | 0.18 s |
| Minute 7 to 16 (IGYO's background tabs asleep) | 0.31 s | 0.21 s | 0.13 s | 0.09 s |

To see where it goes, the processor time of each process was read twice, 151 seconds apart, starting about 17 minutes
after loading (by hand that once; `bench.ps1` has kept it by kind of process since):

| In those 151 seconds | IGYO, in use | IGYO, in the background | IGYO, setting off | Chrome 157 |
|---|---|---|---|---|
| Graphics process | 0.72 s | 0.30 s | 0.36 s | 0.12 s |
| Page processes | 0.16 s | 0.14 s | 0.06 s | 0.11 s |
| All other processes | 0.00 s | 0.02 s | 0.00 s | 0.02 s |
| All | 0.88 s | 0.45 s | 0.42 s | 0.25 s |

IGYO's graphics process used more than Chrome's in every copy. Why was not found out. Whether the differences
between the copies of IGYO come from the setting was not settled either: in two shorter checks with the setting on
and off side by side, the totals were 0.25 and 0.25 s a minute with one tab, and 0.37 and 0.24 s with ten.

## Connections seen

About every ten seconds during the one-tab and ten-tab readings, the processes of each browser were looked at for TCP
connections to other computers. This shows what was connected at those moments, not everything that was sent.

- IGYO, in use: `2620:1ec:33:1::11:443` (registered to Microsoft Corporation), `4.203.73.113:443` (registered to Microsoft Corporation), `4.228.132.40:443` (registered to Microsoft Corporation)
- IGYO, in the background: `2620:1ec:33::11:443` (registered to Microsoft Corporation), `2620:1ec:33:1::11:443` (registered to Microsoft Corporation), `4.203.73.113:443` (registered to Microsoft Corporation), `4.228.132.40:443` (registered to Microsoft Corporation)
- IGYO, setting off: `2620:1ec:33::11:443` (registered to Microsoft Corporation), `4.203.73.113:443` (registered to Microsoft Corporation), `4.228.132.40:443` (registered to Microsoft Corporation)
- Chrome, with the switches above: none

IGYO's own code has nothing that connects in this situation (suggestions were off and the ad block list was not being
updated). Which of the processes made each connection was not recorded.

## What this does not show

- One PC, one run of each reading (start-up: 7 runs). The figures will differ elsewhere.
- Made-up pages that do almost nothing. Real sites were not measured.
- Windows that are not on screen. How much of what was let go a page takes back while it is being drawn and scrolled
  was not measured.
- A PC with plenty of free memory. What letting go is worth when memory is short was not measured.
- New profiles. A profile with history, extensions or many open windows behaves differently.
- Chrome's own memory saver was left at its default. In these 32 minutes Chrome's memory in use stayed between
  392 and 401 MB; what it does after a longer time was not measured.
- Chrome with another window in front of it was not measured.
- Waking a sleeping tab and reloading an unloaded one both take time. That time was not measured.
- Measured on 2026-11-14 (UTC). The copy of IGYO that was measured differs from the released one in one place, and the
  readings were not taken again afterwards: the version text inside its program file, which read
  "1.0.1" followed by the identifier of a source revision and reads "1.0.1" in the released one.
