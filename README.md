# RefreshSwitcher
Automatic refresh rate switching for Windows. Low Hz on the desktop, full Hz in games, battery-friendly Hz when unplugged. Per-monitor rates, automatic game detection for Steam, Epic, GOG and Xbox, safe switching with rollback. Works without VRR or Windows DRR. 

High refresh rate monitors run at full speed all the time, even when you are just reading a web page. That costs power and, on some GPUs, keeps the graphics memory clocked up at idle. RefreshSwitcher lowers the refresh rate while you work and switches back to full speed the moment a game starts.

It works with fixed refresh rates, so it also helps where Windows Dynamic Refresh Rate (DRR) or VRR does not: for example when DRR causes stutter or frame pacing issues, or when your GPU stays at high idle power despite FreeSync/G-Sync.

# Features
Per-monitor rates: set a desktop rate and a game rate for each connected monitor
Automatic game detection
games installed through Steam, Epic, GOG and the Xbox app are detected automatically by their install folder
your own game list: pick the .exe in Explorer or choose from running apps
optional detection of borderless fullscreen apps, with a configurable delay and an exclusion list
Battery mode: separate rate for laptops and handhelds when unplugged
Fixed modes: lock to game mode or desktop mode from the window or the tray
Safe switching: every change is verified after applying it. Failed monitors are rolled back and retried a few times, never in an endless loop
Diagnostics view and log file for troubleshooting
Tray app with autostart option
5 languages: English, Deutsch, Español, Français, Українська
6 themes: Black, Ocean, Forest, Sunset, Graphite, Light
Requirements
Windows 10 or 11
.NET Framework 4.5 or newer (included in Windows 10 and 11)
Installation

Download the latest RefreshSwitcher-Setup-x.y.z.exe from Releases and run it.

The setup installs to C:\Program Files\RefreshSwitcher and can add a Start menu shortcut, a desktop shortcut and autostart. Uninstall from Settings → Apps.

Windows SmartScreen may warn about an unknown publisher because the setup is not code-signed. Choose More info → Run anyway.

# How it works
RefreshSwitcher checks every few seconds whether a game is running.
Games from your list or from a game library switch immediately, usually before the game window appears.
Fullscreen apps only count as a game after they stayed in fullscreen for a few seconds.
After the game ends, it waits a short delay and switches back to the desktop rate.
While a game runs in exclusive fullscreen with its own display mode, RefreshSwitcher does not interfere.

Only refresh rates that your driver offers at the current resolution are used.

# Privacy and security
No network access, no telemetry, no updates in the background
Runs as a normal user, never asks for admin rights (only the installer does)
Never starts other programs. The only exception is Open log, which opens the log file in Notepad
Settings: %APPDATA%\RefreshSwitcher\settings.ini
Log: %APPDATA%\RefreshSwitcher\RefreshSwitcher.log
Building from source

Everything needed is part of Windows, apart from NSIS for the installer.

bat
:: App
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize /win32icon:app.ico /out:RefreshSwitcher.exe RefreshSwitcher.cs

:: Tests
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /main:RefreshSwitcher.Tests /out:Tests.exe RefreshSwitcher.cs Tests.cs
Tests.exe

:: Installer (NSIS: winget install NSIS.NSIS)
makensis installer.nsi


# License 
Copyright © 2026 SmokingTux
RefreshSwitcher is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License v3.0 or any later version. See LICENSE for the full text.
This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
---
Created by SmokingTux
