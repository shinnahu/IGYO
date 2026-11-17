@echo off
rem Tries the new IGYO straight from this folder, WITHOUT installing it:
rem  - it uses its own, separate data folder ("%LOCALAPPDATA%\IGYO trial"), not your profiles, bookmarks or sign-ins;
rem  - the IGYO you have installed is not changed, and can stay open next to it.
rem Close the window to end the trial. Delete "%LOCALAPPDATA%\IGYO trial" whenever you like.
rem (It starts the Debug build in src\IGYO\bin\Debug with the engine in runtime\.)
setlocal
if not defined NSB_DATA_DIR set "NSB_DATA_DIR=%LOCALAPPDATA%\IGYO trial"
for /d %%D in ("%~dp0runtime\*") do if exist "%%D\msedgewebview2.exe" set "NSB_RUNTIME_DIR=%%D"
set "IGYO_EXE=%~dp0src\IGYO\bin\Debug\IGYO.exe"
if not defined NSB_RUNTIME_DIR echo The engine is missing from "%~dp0runtime". & pause & exit /b 1
if not exist "%IGYO_EXE%" echo IGYO hasn't been built yet: "%IGYO_EXE%" is missing. & pause & exit /b 1
start "" "%IGYO_EXE%" %*
