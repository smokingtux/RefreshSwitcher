// =============================================================================
// RefreshSwitcher
// Version 1.8.1, October 2026
//
// Author:   SmokingTux
// Contact:  issues page of the RefreshSwitcher repository on GitHub
//
// Description:
//   Windows tray application that switches the refresh rate of every connected
//   monitor automatically:
//     * a low "desktop" rate while no game is running. Saves power and, on some
//       GPUs (for example AMD RDNA3 with high refresh monitors), lets the video
//       memory clock down at idle;
//     * a high "game" rate as soon as a game is detected;
//     * an optional "battery" rate on laptops and handhelds when unplugged.
//   Games are detected through a user-defined list of executables, through the
//   install folders of Steam, Epic, GOG and the Xbox app, and optionally through
//   borderless fullscreen windows. Every switch is verified afterwards and rolled
//   back on failure, with a limited number of retries.
//
//   The file is ordered top-down:
//     Win32 interop, monitor access, game detection, localisation, settings,
//     testable decision logic, theming, custom controls, main window,
//     diagnostics window, logging, entry point.
//   Classes without Windows calls (RateLogic, SwitchPolicy, RetryState,
//   LibraryLogic, Settings.ParseLines, Palette) are covered by Tests.cs.
//
// Build:
//   Windows, with the .NET Framework compiler that ships with Windows:
//     csc /target:winexe /optimize /win32icon:app.ico /out:RefreshSwitcher.exe RefreshSwitcher.cs
//   Linux, with Mono:
//     mcs -langversion:5 -target:winexe -sdk:4.5 -optimize -win32icon:app.ico
//         -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Core.dll
//         -out:RefreshSwitcher.exe RefreshSwitcher.cs
//   The code sticks to C# 5 on purpose, so the built-in Windows compiler can build it.
//
// Legal:
//   Copyright (C) 2026 SmokingTux
//   SPDX-License-Identifier: GPL-3.0-or-later
//   This program is free software: you can redistribute it and/or modify it under
//   the terms of the GNU General Public License as published by the Free Software
//   Foundation, either version 3 of the License, or (at your option) any later
//   version. It is distributed WITHOUT ANY WARRANTY. See LICENSE for details.
// =============================================================================

using System;                       // base types, Math, DateTime, exceptions
using System.Collections.Generic;   // List, Dictionary, HashSet
using System.Diagnostics;           // Process: game detection and opening the log in Notepad
using System.Drawing;               // colours, fonts, geometry for the custom drawn UI
using System.Drawing.Drawing2D;     // rounded shapes and anti-aliasing
using System.Drawing.Text;          // InstalledFontCollection: check whether Bahnschrift exists
using System.Globalization;         // invariant formats in the log, English .NET error texts
using System.IO;                    // settings, log, game library folders
using System.Linq;                  // list queries in settings, detection and layout
using System.Runtime.InteropServices; // P/Invoke into user32, kernel32 and dwmapi
using System.Text;                  // StringBuilder for Win32 strings, file encodings
using System.Threading;             // single-instance mutex and the --exit signal
using System.Windows.Forms;         // window, tray icon, timer, base controls
using Microsoft.Win32;              // registry (autostart, Steam/GOG paths), power and display events

[assembly: System.Reflection.AssemblyTitle("RefreshSwitcher")]
[assembly: System.Reflection.AssemblyProduct("RefreshSwitcher")]
[assembly: System.Reflection.AssemblyCompany("SmokingTux")]
[assembly: System.Reflection.AssemblyCopyright("© 2026 SmokingTux")]
[assembly: System.Reflection.AssemblyDescription("Automatically switches monitors between desktop and game refresh rates")]
[assembly: System.Reflection.AssemblyVersion("1.8.1.0")]
// Load native DLLs only from System32, never from the folder next to the exe (prevents DLL hijacking).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
[assembly: System.Reflection.AssemblyFileVersion("1.8.1.0")]

namespace RefreshSwitcher
{
    // ------------------------------------------------------------------ Win32

    // Win32 functions and structures used by the app. Struct layouts must match the native definitions.
    static class Native
    {
        // DEVMODE: describes one display mode (resolution, colour depth, refresh rate, position).
        // Used with EnumDisplaySettings and ChangeDisplaySettingsEx. Layout must match the Win32 struct exactly.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        // DISPLAY_DEVICE: one display adapter output as returned by EnumDisplayDevices (\\.\DISPLAY1 etc.).
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        // Plain Win32 rectangle in screen coordinates.
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        // Monitor bounds and work area, filled by GetMonitorInfo. Used by the fullscreen check.
        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

        // Locally unique identifier of a display adapter (DisplayConfig API).
        [StructLayout(LayoutKind.Sequential)]
        public struct LUID { public uint LowPart; public int HighPart; }

        // Source side (GDI view) of an active display path.
        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

        // Refresh rate as a fraction, e.g. 59940/1000.
        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

        // Target side (the physical monitor) of an active display path.
        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        // One active display path: which adapter output drives which monitor.
        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // Mode entry from QueryDisplayConfig. Only the header is read, so the union part is padded to the native size of 64 bytes.
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        public struct DISPLAYCONFIG_MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }

        // Common header for DisplayConfigGetDeviceInfo requests.
        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

        // Request/answer for the GDI device name (\\.\DISPLAYn) of a display path.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        // Request/answer for the monitor's friendly name (e.g. "AW3225QF") and its stable device path.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        // Lists display outputs; used to find every monitor attached to the desktop.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
        // Reads display modes: the current one, the one saved in the registry, or mode N of the supported list.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
        // Stages a new mode for one monitor (CDS_NORESET) so several monitors can be changed together.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
        // Same function with null arguments: applies all staged changes at once. Separate entry point because the
        // managed signature differs (no device name, no DEVMODE).
        [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", ExactSpelling = true)]
        public static extern int ChangeDisplaySettingsExApply(IntPtr lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        // DisplayConfig API: needed only to read monitor model names and stable device paths.
        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);
        // Two overloads of the same native function, one per request type.
        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);
        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        // Foreground window, its size and its monitor: used to detect borderless fullscreen games.
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
        // Maps a window to its process so the fullscreen app can be named and filtered.
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        // Window class name: lets the fullscreen check skip the desktop and the taskbar.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        // Window style: windows with a title bar are never treated as fullscreen games.
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        // Opt into system DPI awareness so layout and window rectangles use real pixels.
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        // Releases the native icon handle created for tray icons (avoids a GDI handle leak).
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        // Used for WM_SETREDRAW to freeze painting while the main window scrolls.
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] public static extern IntPtr SendMessagePtr(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        // Dark or light title bar and caption colours that match the selected theme (Windows 10 2004+ / Windows 11).
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        // Process image path lookup for game-library detection. PROCESS_QUERY_LIMITED_INFORMATION also works
        // for most processes started by other users or with anti-cheat, unlike Process.MainModule.
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    }

    // ------------------------------------------------------------------ Monitors

    // Snapshot of one monitor attached to the desktop.
    // Key is the stable device path (survives reboots and port changes better than \\.\DISPLAYn) and is used
    // to store per-monitor settings. Rates lists every refresh rate available at the desktop resolution.
    class MonitorInfo
    {
        public string GdiName;
        public string FriendlyName;
        public string Key;
        public int Width, Height;
        public int RegistryHz;   // desktop setting saved in the registry (target state)
        public int ActiveHz;     // rate actually active right now (actual state; differs while a game uses exclusive fullscreen)
        public int[] Rates = new int[0];
    }

    // Reads and changes monitor modes through the classic GDI display API.
    // Changes are "staged" per monitor and applied together, so multi-monitor setups flicker only once.
    static class Display
    {
        const int ENUM_CURRENT_SETTINGS = -1;
        const int ENUM_REGISTRY_SETTINGS = -2;
        const int ATTACHED_TO_DESKTOP = 0x1;
        const int DM_POSITION = 0x20;
        const int DM_BITSPERPEL = 0x40000;
        const int DM_PELSWIDTH = 0x80000;
        const int DM_PELSHEIGHT = 0x100000;
        const int DM_DISPLAYFREQUENCY = 0x400000;
        const uint CDS_UPDATEREGISTRY = 0x1;
        const uint CDS_NORESET = 0x10000000;
        const uint QDC_ONLY_ACTIVE_PATHS = 0x2;

        // Creates a DEVMODE with dmSize set, which every Win32 call requires.
        static Native.DEVMODE NewDevMode()
        {
            var dm = new Native.DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE));
            return dm;
        }

        // Creates a DISPLAY_DEVICE with cb set, which EnumDisplayDevices requires.
        static Native.DISPLAY_DEVICE NewDevice()
        {
            var dd = new Native.DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(Native.DISPLAY_DEVICE));
            return dd;
        }

        // Returns all monitors attached to the desktop with their saved and active refresh rates.
        // Friendly names come from the DisplayConfig API; if that fails the monitor is called "Monitor N".
        public static List<MonitorInfo> GetMonitors()
        {
            var names = GetFriendlyNames();
            var list = new List<MonitorInfo>();
            var dd = NewDevice();
            for (uint i = 0; Native.EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if ((dd.StateFlags & ATTACHED_TO_DESKTOP) != 0)
                {
                    var m = new MonitorInfo { GdiName = dd.DeviceName };
                    var dm = NewDevMode();
                    if (Native.EnumDisplaySettings(dd.DeviceName, ENUM_REGISTRY_SETTINGS, ref dm))
                    {
                        m.Width = dm.dmPelsWidth;
                        m.Height = dm.dmPelsHeight;
                        m.RegistryHz = dm.dmDisplayFrequency;
                    }
                    var cur = NewDevMode();
                    m.ActiveHz = Native.EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref cur) ? cur.dmDisplayFrequency : m.RegistryHz;
                    m.Rates = GetRates(dd.DeviceName);
                    string[] info;
                    if (names.TryGetValue(dd.DeviceName, out info))
                    {
                        m.FriendlyName = info[0];
                        m.Key = info[1];
                    }
                    if (string.IsNullOrEmpty(m.FriendlyName)) m.FriendlyName = "Monitor " + (list.Count + 1);
                    if (string.IsNullOrEmpty(m.Key)) m.Key = dd.DeviceName;
                    list.Add(m);
                }
                dd = NewDevice();
            }
            return list;
        }

        // Maps the GDI name (\\.\DISPLAY1) to { model name, stable device path }
        static Dictionary<string, string[]> GetFriendlyNames()
        {
            var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                uint np, nm;
                if (Native.GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return result;
                var paths = new Native.DISPLAYCONFIG_PATH_INFO[np];
                var modes = new Native.DISPLAYCONFIG_MODE_INFO[nm];
                if (Native.QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return result;
                for (int i = 0; i < np; i++)
                {
                    var src = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    src.header.type = 1;
                    src.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME));
                    src.header.adapterId = paths[i].sourceInfo.adapterId;
                    src.header.id = paths[i].sourceInfo.id;
                    if (Native.DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                    var tgt = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    tgt.header.type = 2;
                    tgt.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_TARGET_DEVICE_NAME));
                    tgt.header.adapterId = paths[i].targetInfo.adapterId;
                    tgt.header.id = paths[i].targetInfo.id;
                    if (Native.DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;

                    if (!result.ContainsKey(src.viewGdiDeviceName))
                        result[src.viewGdiDeviceName] = new[] { tgt.monitorFriendlyDeviceName, tgt.monitorDevicePath };
                }
            }
            catch { }
            return result;
        }

        // Returns every refresh rate the driver offers at the saved desktop resolution and colour depth, sorted ascending.
        static int[] GetRates(string dev)
        {
            var result = new List<int>();
            var cur = NewDevMode();
            if (!Native.EnumDisplaySettings(dev, ENUM_REGISTRY_SETTINGS, ref cur)) return result.ToArray();
            var dm = NewDevMode();
            for (int i = 0; Native.EnumDisplaySettings(dev, i, ref dm); i++)
            {
                if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight &&
                    dm.dmBitsPerPel == cur.dmBitsPerPel && dm.dmDisplayFrequency > 1 &&
                    !result.Contains(dm.dmDisplayFrequency))
                    result.Add(dm.dmDisplayFrequency);
                dm = NewDevMode();
            }
            result.Sort();
            return result.ToArray();
        }

        // Stages a refresh rate for one monitor in the registry without applying it yet (CDS_NORESET).
        // Resolution, colour depth and position are kept. Returns a DISP_CHANGE_* code, 0 means success.
        public static int StageHz(string dev, int hz)
        {
            var dm = NewDevMode();
            if (!Native.EnumDisplaySettings(dev, ENUM_REGISTRY_SETTINGS, ref dm)) return -99;
            dm.dmDisplayFrequency = hz;
            dm.dmFields = DM_POSITION | DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            return Native.ChangeDisplaySettingsEx(dev, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        }

        // Translates DISP_CHANGE_* return codes into readable names for the log and the UI.
        public static string DispName(int rc)
        {
            switch (rc)
            {
                case 0: return "SUCCESSFUL";
                case 1: return "RESTART";
                case -1: return "FAILED";
                case -2: return "BADMODE";
                case -3: return "NOTUPDATED";
                case -4: return "BADFLAGS";
                case -5: return "BADPARAM";
                case -6: return "BADDUALVIEW";
                case -99: return "NO_SETTINGS";
                default: return "CODE_" + rc;
            }
        }

        // Applies all staged changes in one step. Returns a DISP_CHANGE_* code.
        public static int ApplyStaged()
        {
            return Native.ChangeDisplaySettingsExApply(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        }
    }

    // ------------------------------------------------------------------ Game detection

    // Pure helpers for game-library detection: anything started from a Steam, Epic, GOG or Xbox game folder counts
    // as a game, except helper processes and known non-game tools. No Windows calls, covered by Tests.cs.
    static class LibraryLogic
    {
        // Folders of programs that are installed through Steam but are not games
        static readonly string[] NonGameFolders = { "\\wallpaper_engine\\", "\\lossless scaling\\", "\\steamvr\\", "\\steamworks shared\\",
            "\\_commonredist\\", "\\commonredist\\", "\\redist\\", "\\easyanticheat\\", "\\battleye\\", "\\soundpad\\" };

        // Name parts of helper processes inside game folders: crash reporters, installers, launchers, anti-cheat
        static readonly string[] HelperTokens = { "crash", "unins", "redist", "setup", "launcher", "updater", "report",
            "easyanticheat", "battleye", "beservice", "cefprocess", "webhelper", "install", "prereq", "dxsetup", "dotnet", "eac_" };

        // Extracts every library path from Steam's libraryfolders.vdf (plain text key/value format).
        public static List<string> ParseSteamLibraryFolders(string vdf)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(vdf)) return list;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\""))
                list.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            return list;
        }

        // Extracts InstallLocation from an Epic Games Launcher manifest (*.item, JSON). Returns null if missing.
        public static string ParseEpicInstallLocation(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"InstallLocation\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\/", "/") : null;
        }

        // Normalises a folder path to backslashes with a trailing backslash, so prefix checks cannot match
        // "common2" when looking for "common".
        public static string NormalizeRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string r = path.Trim().Replace('/', '\\');
            return r.EndsWith("\\") ? r : r + "\\";
        }

        // True if the file path lies inside one of the given library roots (case-insensitive).
        public static bool IsUnder(string path, IEnumerable<string> roots)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (var r in roots) if (path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // True for helper processes that live in a game folder but are not the game itself:
        // crash reporters, installers, launchers, anti-cheat, and known non-game Steam tools.
        public static bool IsHelper(string processName, string path)
        {
            string n = (processName ?? "").ToLowerInvariant();
            string p = (path ?? "").ToLowerInvariant();
            if (HelperTokens.Any(t => n.Contains(t))) return true;
            return NonGameFolders.Any(f => p.Contains(f));
        }
    }

    // Finds the install folders of game launchers on this PC (registry and launcher files).
    static class LibraryScanner
    {
        // Adds a root once, only if the folder exists.
        static void AddRoot(List<string> roots, string path)
        {
            string r = LibraryLogic.NormalizeRoot(path);
            try
            {
                if (r != null && Directory.Exists(r) && !roots.Any(x => x.Equals(r, StringComparison.OrdinalIgnoreCase))) roots.Add(r);
            }
            catch { }
        }

        // Finds all game install folders on this PC: every Steam library, Epic install locations, GOG games
        // and XboxGames folders. Each launcher is scanned separately so one broken source does not stop the rest.
        public static List<string> FindRoots()
        {
            var roots = new List<string>();
            // Steam: every library listed in libraryfolders.vdf, plus the main Steam folder
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string steam = k != null ? k.GetValue("SteamPath") as string : null;
                    if (!string.IsNullOrEmpty(steam))
                    {
                        steam = steam.Replace('/', '\\');
                        AddRoot(roots, Path.Combine(steam, "steamapps", "common"));
                        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                        if (File.Exists(vdf))
                            foreach (var lib in LibraryLogic.ParseSteamLibraryFolders(File.ReadAllText(vdf)))
                                AddRoot(roots, Path.Combine(lib, "steamapps", "common"));
                    }
                }
            }
            catch (Exception ex) { Log.Write("Steam library scan failed: " + ex.Message); }

            // Epic: install folder of each game from the launcher manifests
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*.item"))
                        AddRoot(roots, LibraryLogic.ParseEpicInstallLocation(File.ReadAllText(f)));
            }
            catch (Exception ex) { Log.Write("Epic library scan failed: " + ex.Message); }

            // GOG: install paths from the registry (GOG Galaxy and offline installers)
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
                    if (k != null)
                        foreach (var id in k.GetSubKeyNames())
                            using (var g = k.OpenSubKey(id))
                                if (g != null) AddRoot(roots, g.GetValue("path") as string);
            }
            catch (Exception ex) { Log.Write("GOG library scan failed: " + ex.Message); }

            // Xbox app / Game Pass: default "XboxGames" folder on every fixed drive
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                    if (d.DriveType == DriveType.Fixed && d.IsReady) AddRoot(roots, Path.Combine(d.RootDirectory.FullName, "XboxGames"));
            }
            catch (Exception ex) { Log.Write("Xbox library scan failed: " + ex.Message); }

            return roots;
        }
    }

    // Detects running games in three ways: by name from the user's list, by install folder (game libraries)
    // and by a borderless window covering a whole monitor.
    static class GameDetector
    {
        static readonly Dictionary<string, string> pathCache = new Dictionary<string, string>();

        // Returns the full exe path of a process, or an empty string if Windows denies access.
        static string ImagePath(int pid)
        {
            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            IntPtr h = Native.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { Native.CloseHandle(h); }
        }

        // Returns the first running process in this user session whose exe lies in a game library folder,
        // skipping helpers and ignored apps. Paths are cached per process to keep the 2 s polling cheap.
        public static string FindLibraryGame(IList<string> roots, ICollection<string> ignore)
        {
            if (roots == null || roots.Count == 0) return null;
            string found = null;
            var alive = new HashSet<string>();
            int session = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (found != null || p.SessionId != session) continue;
                    string key = p.Id + ":" + p.ProcessName;
                    alive.Add(key);
                    string path;
                    if (!pathCache.TryGetValue(key, out path)) { path = ImagePath(p.Id); pathCache[key] = path; }
                    if (!LibraryLogic.IsUnder(path, roots)) continue;
                    if (LibraryLogic.IsHelper(p.ProcessName, path)) continue;
                    if (ignore.Any(i => string.Equals(i, p.ProcessName, StringComparison.OrdinalIgnoreCase))) continue;
                    found = p.ProcessName;
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (found == null)
                foreach (var k in pathCache.Keys.Where(k => !alive.Contains(k)).ToList()) pathCache.Remove(k);
            return found;
        }

        // Returns the first name from the user's game list that currently has a running process, or null.
        public static string FindRunningGame(IEnumerable<string> names)
        {
            foreach (var n in names)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(n); }
                catch { continue; }
                bool any = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (any) return n;
            }
            return null;
        }

        // Returns the process name of a borderless fullscreen foreground window, unless it is ignored or RefreshSwitcher itself.
        public static string FindFullscreenApp(ICollection<string> ignore)
        {
            uint pid = GetFullscreenPid();
            if (pid == 0) return null;
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                {
                    if (p.Id == Process.GetCurrentProcess().Id) return null;
                    if (ignore.Any(i => string.Equals(i, p.ProcessName, StringComparison.OrdinalIgnoreCase))) return null;
                    return p.ProcessName;
                }
            }
            catch { return null; }
        }

        // PID of the foreground window if it covers its whole monitor and has no title bar, otherwise 0.
        // Desktop, taskbar and normal maximised windows are excluded.
        static uint GetFullscreenPid()
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;

            var sb = new StringBuilder(256);
            Native.GetClassName(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return 0;

            const int GWL_STYLE = -16;
            const int WS_CAPTION = 0x00C00000;
            if ((Native.GetWindowLong(hwnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return 0;

            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return 0;
            IntPtr mon = Native.MonitorFromWindow(hwnd, 2);
            var mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            if (!Native.GetMonitorInfo(mon, ref mi)) return 0;

            if (r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom)
            {
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                return pid;
            }
            return 0;
        }
    }

    // ------------------------------------------------------------------ Localisation

    // UI languages. The numbers are the column index in the text table of L.
    enum Lang { En = 0, De = 1, Es = 2, Fr = 3, Uk = 4 }

    // Localisation. Every UI text has a key and one entry per language in Lang order
    // (English, German, Spanish, French, Ukrainian). The log always stays English.
    static class L
    {
        public static Lang Current = Lang.En;

        static readonly Dictionary<string, string[]> Texts = new Dictionary<string, string[]>
        {
            { "status.desktop", new[] { "Desktop", "Desktop", "Escritorio", "Bureau", "Робочий стіл" } },
            { "status.waiting", new[] { "Waiting for a game", "Wartet auf ein Spiel", "Esperando un juego", "En attente d’un jeu", "Очікування гри" } },
            { "status.game", new[] { "Game running", "Spiel läuft", "Juego en ejecución", "Jeu en cours", "Гра запущена" } },
            { "status.backIn", new[] { "Back to desktop mode in {0} s", "Zurück in den Desktopmodus in {0} s", "Vuelta al modo escritorio en {0} s", "Retour au mode bureau dans {0} s", "Повернення до робочого столу через {0} с" } },
            { "status.fixedGame", new[] { "Game mode", "Spielmodus", "Modo juego", "Mode jeu", "Ігровий режим" } },
            { "status.fixedDesktop", new[] { "Desktop mode", "Desktopmodus", "Modo escritorio", "Mode bureau", "Режим робочого столу" } },
            { "status.fixed", new[] { "Locked", "Fest eingestellt", "Fijado", "Verrouillé", "Зафіксовано" } },
            { "mode.auto", new[] { "Automatic", "Automatisch", "Automático", "Automatique", "Автоматично" } },
            { "mode.max", new[] { "Game mode", "Spielmodus", "Modo juego", "Mode jeu", "Ігровий режим" } },
            { "mode.idle", new[] { "Desktop mode", "Desktopmodus", "Modo escritorio", "Mode bureau", "Робочий стіл" } },
            { "mon.heading", new[] { "Monitors", "Monitore", "Monitores", "Écrans", "Монітори" } },
            { "mon.hint", new[] { "Desktop: refresh rate without a game. Game: refresh rate while one is running. Change with the arrows or mouse wheel.", "Desktop: Bildwiederholrate ohne Spiel. Spiel: Bildwiederholrate, solange eins läuft. Ändern per Pfeil oder Mausrad.", "Escritorio: frecuencia sin juego. Juego: frecuencia mientras uno se ejecuta. Cámbiala con las flechas o la rueda del ratón.", "Bureau : fréquence sans jeu. Jeu : fréquence pendant qu’un jeu tourne. Modifiez-la avec les flèches ou la molette.", "Робочий стіл: частота без гри. Гра: частота, поки гра запущена. Змінюйте стрілками або коліщатком миші." } },
            { "mon.none", new[] { "No monitors found.", "Keine Monitore gefunden.", "No se encontraron monitores.", "Aucun écran trouvé.", "Монітори не знайдено." } },
            { "step.desktop", new[] { "Desktop", "Desktop", "Escritorio", "Bureau", "Робочий стіл" } },
            { "step.game", new[] { "Game", "Spiel", "Juego", "Jeu", "Гра" } },
            { "games.heading", new[] { "Games", "Spiele", "Juegos", "Jeux", "Ігри" } },
            { "games.hint", new[] { "When one of these is running, all monitors switch to game mode.", "Läuft eins davon, wechseln alle Monitore in den Spielmodus.", "Si uno de estos se ejecuta, todos los monitores pasan al modo juego.", "Quand l’un d’eux est lancé, tous les écrans passent en mode jeu.", "Коли одна з них запущена, усі монітори переходять в ігровий режим." } },
            { "games.empty", new[] { "No games added yet.", "Noch keine Spiele eingetragen.", "Aún no hay juegos.", "Aucun jeu ajouté.", "Ще немає ігор." } },
            { "games.add", new[] { "Choose .exe…", "EXE auswählen…", "Elegir .exe…", "Choisir un .exe…", "Вибрати .exe…" } },
            { "games.remove", new[] { "Remove", "Entfernen", "Quitar", "Retirer", "Видалити" } },
            { "games.pick", new[] { "From running apps", "Aus laufenden Apps", "De apps abiertas", "Depuis les apps ouvertes", "Із запущених програм" } },
            { "games.pickNone", new[] { "No apps with a window found", "Keine Apps mit Fenster gefunden", "No hay apps con ventana", "Aucune app avec fenêtre", "Немає програм із вікном" } },
            { "beh.heading", new[] { "Behavior", "Verhalten", "Comportamiento", "Comportement", "Поведінка" } },
            { "beh.fullscreen", new[] { "Treat borderless fullscreen apps as games", "Randlose Vollbild-Apps wie Spiele behandeln", "Tratar apps a pantalla completa sin bordes como juegos", "Traiter les apps plein écran sans bordure comme des jeux", "Вважати повноекранні програми без рамки іграми" } },
            { "beh.delay", new[] { "Back to desktop mode after the game ends", "Zurück in den Desktopmodus nach Spielende", "Volver al modo escritorio tras el juego", "Retour au mode bureau après le jeu", "Повернення до робочого столу після гри" } },
            { "beh.autostart", new[] { "Start with Windows", "Mit Windows starten", "Iniciar con Windows", "Démarrer avec Windows", "Запускати разом із Windows" } },
            { "beh.tray", new[] { "Start in the notification area only", "Beim Start nur im Infobereich anzeigen", "Iniciar solo en el área de notificación", "Démarrer uniquement dans la zone de notification", "Запускати лише в області сповіщень" } },
            { "beh.language", new[] { "Language", "Sprache", "Idioma", "Langue", "Мова" } },
            { "footer.none", new[] { "Not switched yet", "Noch nicht umgeschaltet", "Aún sin cambios", "Pas encore de changement", "Ще не перемикалося" } },
            { "footer.switched", new[] { "Switched to {1} at {0:HH:mm}{2}", "Um {0:HH:mm} in den {1} gewechselt{2}", "Cambiado a {1} a las {0:HH:mm}{2}", "Passé en {1} à {0:HH:mm}{2}", "Перемкнено: {1} о {0:HH:mm}{2}" } },
            { "footer.gameRate", new[] { "game mode", "Spielmodus", "modo juego", "mode jeu", "ігровий режим" } },
            { "footer.desktopRate", new[] { "desktop mode", "Desktopmodus", "modo escritorio", "mode bureau", "режим робочого столу" } },
            { "footer.because", new[] { " ({0}.exe)", " ({0}.exe)", " ({0}.exe)", " ({0}.exe)", " ({0}.exe)" } },
            { "footer.failed", new[] { "Switching failed for {0}.", "Umschalten fehlgeschlagen bei {0}.", "Error al cambiar: {0}.", "Échec du changement : {0}.", "Не вдалося перемкнути: {0}." } },
            { "tray.open", new[] { "Open", "Öffnen", "Abrir", "Ouvrir", "Відкрити" } },
            { "tray.max", new[] { "Game mode", "Spielmodus", "Modo juego", "Mode jeu", "Ігровий режим" } },
            { "tray.idle", new[] { "Desktop mode", "Desktopmodus", "Modo escritorio", "Mode bureau", "Режим робочого столу" } },
            { "tray.exit", new[] { "Exit", "Beenden", "Salir", "Quitter", "Вийти" } },
            { "tray.balloon", new[] { "Still running in the notification area. Exit via the icon there.", "Läuft im Infobereich weiter. Beenden über das Symbol dort.", "Sigue ejecutándose en el área de notificación. Sal desde su icono.", "Toujours actif dans la zone de notification. Quittez via son icône.", "Програма працює далі в області сповіщень. Вийти можна через її значок." } },
            { "mon.include", new[] { "Switch automatically", "Automatisch umschalten", "Cambiar automáticamente", "Changer automatiquement", "Перемикати автоматично" } },
            { "status.detectedList", new[] { "{0}.exe detected (game list)", "{0}.exe erkannt (Spieleliste)", "{0}.exe detectado (lista de juegos)", "{0}.exe détecté (liste de jeux)", "Виявлено {0}.exe (список ігор)" } },
            { "status.detectedFs", new[] { "{0}.exe detected (fullscreen)", "{0}.exe erkannt (Vollbild)", "{0}.exe detectado (pantalla completa)", "{0}.exe détecté (plein écran)", "Виявлено {0}.exe (повний екран)" } },
            { "status.pending", new[] { "{0}.exe in fullscreen, switching in {1} s", "{0}.exe im Vollbild, Wechsel in {1} s", "{0}.exe a pantalla completa, cambio en {1} s", "{0}.exe en plein écran, changement dans {1} s", "{0}.exe на весь екран, перемикання через {1} с" } },
            { "status.partial", new[] { "Not switched: {0}", "Nicht umgeschaltet: {0}", "Sin cambiar: {0}", "Non changé : {0}", "Не перемкнено: {0}" } },
            { "err.rejected", new[] { "Windows rejected {0} Hz ({1})", "Windows hat {0} Hz abgelehnt ({1})", "Windows rechazó {0} Hz ({1})", "Windows a refusé {0} Hz ({1})", "Windows відхилила {0} Hz ({1})" } },
            { "err.notActive", new[] { "{0} Hz was not active after switching", "{0} Hz war nach dem Umschalten nicht aktiv", "{0} Hz no estaba activo tras el cambio", "{0} Hz n’était pas actif après le changement", "Частота {0} Hz не активна після перемикання" } },
            { "footer.retryIn", new[] { "Next attempt in {0} s.", "Nächster Versuch in {0} s.", "Próximo intento en {0} s.", "Nouvel essai dans {0} s.", "Наступна спроба через {0} с." } },
            { "footer.gaveUp", new[] { "Stopped after {0} attempts. Change the mode or a rate to try again.", "Nach {0} Versuchen gestoppt. Modus oder Rate ändern, um es erneut zu versuchen.", "Detenido tras {0} intentos. Cambia el modo o una frecuencia para reintentar.", "Arrêté après {0} essais. Changez le mode ou une fréquence pour réessayer.", "Зупинено після {0} спроб. Змініть режим або частоту, щоб спробувати знову." } },
            { "beh.excluded", new[] { "Excluded from automatic detection: {0}", "Von der automatischen Erkennung ausgenommen: {0}", "Excluidas de la detección automática: {0}", "Exclues de la détection automatique : {0}", "Виключено з автоматичного виявлення: {0}" } },
            { "beh.excludedNone", new[] { "No apps are excluded from automatic detection.", "Keine Apps von der automatischen Erkennung ausgenommen.", "Ninguna app excluida de la detección automática.", "Aucune app exclue de la détection automatique.", "Жодну програму не виключено з автоматичного виявлення." } },
            { "beh.resetIgnore", new[] { "Reset exclusions", "Ausnahmen zurücksetzen", "Restablecer exclusiones", "Réinitialiser les exclusions", "Скинути винятки" } },
            { "beh.fsDelay", new[] { "Fullscreen counts as game after", "Vollbild zählt als Spiel nach", "Pantalla completa cuenta como juego tras", "Le plein écran compte comme jeu après", "Повний екран вважається грою через" } },
            { "beh.poll", new[] { "Check for games every", "Auf Spiele prüfen alle", "Buscar juegos cada", "Rechercher des jeux toutes les", "Перевіряти ігри кожні" } },
            { "beh.diag", new[] { "Diagnostics", "Diagnose", "Diagnóstico", "Diagnostic", "Діагностика" } },
            { "btn.ignore", new[] { "Ignore {0}.exe", "{0}.exe ignorieren", "Ignorar {0}.exe", "Ignorer {0}.exe", "Ігнорувати {0}.exe" } },
            { "tray.diag", new[] { "Diagnostics", "Diagnose", "Diagnóstico", "Diagnostic", "Діагностика" } },
            { "tray.log", new[] { "Open log", "Protokoll öffnen", "Abrir registro", "Ouvrir le journal", "Відкрити журнал" } },
            { "diag.title", new[] { "RefreshSwitcher diagnostics", "RefreshSwitcher Diagnose", "Diagnóstico de RefreshSwitcher", "Diagnostic RefreshSwitcher", "Діагностика RefreshSwitcher" } },
            { "diag.copy", new[] { "Copy", "Kopieren", "Copiar", "Copier", "Копіювати" } },
            { "diag.stale", new[] { "Remove {0} old monitor profiles", "{0} alte Monitorprofile entfernen", "Quitar {0} perfiles antiguos", "Supprimer {0} anciens profils", "Видалити старі профілі: {0}" } },
            { "diag.close", new[] { "Close", "Schließen", "Cerrar", "Fermer", "Закрити" } },
            { "step.battery", new[] { "Battery", "Akku", "Batería", "Batterie", "Батарея" } },
            { "mon.hintBattery", new[] { "Desktop: without a game. Game: while one is running. Battery: on battery power. Change with the arrows or mouse wheel.", "Desktop: ohne Spiel. Spiel: solange eins läuft. Akku: im Akkubetrieb. Ändern per Pfeil oder Mausrad.", "Escritorio: sin juego. Juego: mientras uno se ejecuta. Batería: con batería. Cámbialas con las flechas o la rueda del ratón.", "Bureau : sans jeu. Jeu : pendant qu’un jeu tourne. Batterie : sur batterie. Modifiez avec les flèches ou la molette.", "Робочий стіл: без гри. Гра: поки гра запущена. Батарея: від батареї. Змінюйте стрілками або коліщатком миші." } },
            { "beh.battery", new[] { "Battery mode: save power when unplugged", "Akkumodus: Strom sparen ohne Netzteil", "Modo batería: ahorrar energía sin cargador", "Mode batterie : économiser l’énergie sans chargeur", "Режим батареї: економія енергії без зарядки" } },
            { "beh.batteryOn", new[] { "Running on battery right now. Your screens use the battery setting, even while gaming.", "Gerade im Akkubetrieb. Die Bildschirme nutzen die Akku-Einstellung, auch beim Spielen.", "Con batería ahora. Las pantallas usan el ajuste de batería, también mientras juegas.", "Sur batterie actuellement. Les écrans utilisent le réglage batterie, même en jeu.", "Зараз від батареї. Екрани використовують налаштування батареї, навіть під час гри." } },
            { "beh.batteryAc", new[] { "Plugged in right now. When you unplug, your screens switch to the battery setting, even while gaming.", "Gerade am Netzteil. Ohne Netzteil wechseln die Bildschirme auf die Akku-Einstellung, auch beim Spielen.", "Conectado ahora. Sin cargador, las pantallas pasan al ajuste de batería, también mientras juegas.", "Branché actuellement. Sans chargeur, les écrans passent au réglage batterie, même en jeu.", "Зараз від мережі. Без зарядки екрани переходять на налаштування батареї, навіть під час гри." } },
            { "beh.batteryNone", new[] { "No battery detected on this device.", "Kein Akku in diesem Gerät erkannt.", "No se detectó batería en este equipo.", "Aucune batterie détectée sur cet appareil.", "На цьому пристрої батарею не виявлено." } },
            { "status.battery", new[] { "Battery mode", "Akkumodus", "Modo batería", "Mode batterie", "Режим батареї" } },
            { "status.batterySub", new[] { "Running on battery", "Läuft auf Akku", "Funcionando con batería", "Sur batterie", "Працює від батареї" } },
            { "footer.batteryRate", new[] { "battery mode", "Akkumodus", "modo batería", "mode batterie", "режим батареї" } },
            { "beh.theme", new[] { "Theme", "Design", "Tema", "Thème", "Тема" } },
            { "theme.oled", new[] { "Black", "Schwarz", "Negro", "Noir", "Чорна" } },
            { "theme.ocean", new[] { "Ocean", "Ozean", "Océano", "Océan", "Океан" } },
            { "theme.forest", new[] { "Forest", "Wald", "Bosque", "Forêt", "Ліс" } },
            { "theme.sunset", new[] { "Sunset", "Abendrot", "Atardecer", "Couchant", "Захід" } },
            { "theme.graphite", new[] { "Graphite", "Graphit", "Grafito", "Graphite", "Графіт" } },
            { "theme.light", new[] { "Light", "Hell", "Claro", "Clair", "Світла" } },
            { "games.dialogTitle", new[] { "Choose the game’s .exe", "EXE des Spiels auswählen", "Elige el .exe del juego", "Choisissez le .exe du jeu", "Виберіть .exe гри" } },
            { "games.filter", new[] { "Programs (*.exe)|*.exe", "Programme (*.exe)|*.exe", "Programas (*.exe)|*.exe", "Programmes (*.exe)|*.exe", "Програми (*.exe)|*.exe" } },
            { "games.libraries", new[] { "Detect games from Steam, Epic, GOG and Xbox automatically", "Spiele aus Steam, Epic, GOG und Xbox automatisch erkennen", "Detectar juegos de Steam, Epic, GOG y Xbox automáticamente", "Détecter automatiquement les jeux Steam, Epic, GOG et Xbox", "Автоматично виявляти ігри зі Steam, Epic, GOG і Xbox" } },
            { "games.librariesFound", new[] { "{0} game folders found. Anything started from there counts as a game.", "{0} Spieleordner gefunden. Alles, was von dort startet, zählt als Spiel.", "{0} carpetas de juegos encontradas. Todo lo que se inicie desde ahí cuenta como juego.", "{0} dossiers de jeux trouvés. Tout ce qui y est lancé compte comme un jeu.", "Знайдено тек з іграми: {0}. Усе, що запускається звідти, вважається грою." } },
            { "games.librariesNone", new[] { "No game folders found.", "Keine Spieleordner gefunden.", "No se encontraron carpetas de juegos.", "Aucun dossier de jeux trouvé.", "Теки з іграми не знайдено." } },
            { "status.detectedLib", new[] { "{0}.exe detected (game library)", "{0}.exe erkannt (Spielebibliothek)", "{0}.exe detectado (biblioteca de juegos)", "{0}.exe détecté (bibliothèque de jeux)", "Виявлено {0}.exe (бібліотека ігор)" } },
            { "msg.already", new[] { "RefreshSwitcher is already running. You'll find its icon in the notification area.", "RefreshSwitcher läuft bereits. Das Symbol findest du im Infobereich der Taskleiste.", "RefreshSwitcher ya se está ejecutando. Encontrarás su icono en el área de notificación.", "RefreshSwitcher est déjà lancé. Son icône se trouve dans la zone de notification.", "RefreshSwitcher вже запущено. Значок знаходиться в області сповіщень." } },
            { "msg.crash", new[] { "RefreshSwitcher crashed ({0}):\n\n{1}: {2}\n\nDetails are in:\n{3}", "RefreshSwitcher ist abgestürzt ({0}):\n\n{1}: {2}\n\nDetails stehen in:\n{3}", "RefreshSwitcher se cerró inesperadamente ({0}):\n\n{1}: {2}\n\nDetalles en:\n{3}", "RefreshSwitcher a planté ({0}) :\n\n{1} : {2}\n\nDétails dans :\n{3}", "RefreshSwitcher аварійно завершився ({0}):\n\n{1}: {2}\n\nПодробиці у файлі:\n{3}" } },
        };

        public static IEnumerable<string> Keys { get { return Texts.Keys; } }
        public static int Count(string key) { string[] v; return Texts.TryGetValue(key, out v) ? v.Length : 0; }

        // Text for a key in the current language. Falls back to English, and to the key itself if unknown.
        public static string T(string key)
        {
            string[] v;
            if (!Texts.TryGetValue(key, out v)) return key;
            int i = (int)Current;
            return i >= 0 && i < v.Length && !string.IsNullOrEmpty(v[i]) ? v[i] : v[0];
        }

        // Formatted text, string.Format with the current-language template.
        public static string F(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }
    }

    // ------------------------------------------------------------------ Settings

    // User mode: automatic switching, fixed game rate (Max) or fixed desktop rate (Idle).
    enum SwitchMode { Auto = 0, Max = 1, Idle = 2 }

    // User settings, stored as simple key=value lines in %APPDATA%\RefreshSwitcher\settings.ini.
    // Per-monitor values use the monitor key as suffix, e.g. "idle.<device path>=60".
    class Settings
    {
        // Apps never treated as games by fullscreen or library detection: browsers, video players, shell, wallpaper/scaling tools
        public static readonly string[] DefaultIgnore = { "firefox", "chrome", "msedge", "brave", "opera", "vlc", "mpv", "explorer", "ApplicationFrameHost", "wallpaper32", "wallpaper64", "LosslessScaling" };

        public SwitchMode Mode = SwitchMode.Auto;              // automatic or fixed mode
        public List<string> Games = new List<string>();        // process names (exe without extension) from the user's game list
        public List<string> FullscreenIgnore = new List<string>(DefaultIgnore); // apps excluded from automatic detection
        public bool DetectFullscreen = true;                   // treat borderless fullscreen windows as games
        public bool DetectLibraries = true;                    // treat apps from Steam/Epic/GOG/Xbox folders as games
        public int IdleDelay = 6;                              // seconds after a game ends before the desktop rate returns
        public int FullscreenDelay = 3;                        // seconds a window must stay fullscreen before it counts as a game
        public int PollSeconds = 2;                            // detection interval of the main timer
        public bool StartInTray = false;                       // start hidden in the notification area
        public bool BatteryMode = false;                       // use the battery rate when unplugged
        public ThemeId Theme = ThemeId.Oled;                   // colour theme
        public Lang Language = Lang.En;                        // UI language
        // Per-monitor rates, keyed by the monitor's stable device path
        public Dictionary<string, int> IdleHz = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> MaxHz = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> BatteryHz = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Monitors the user excluded from automatic switching
        public HashSet<string> ExcludedMonitors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Values offered by the pickers in the UI
        public static readonly int[] PollChoices = { 1, 2, 3, 5 };
        public static readonly int[] DelayChoices = { 0, 2, 4, 6, 10, 15, 30, 60 };
        public static readonly int[] FullscreenDelayChoices = { 0, 1, 2, 3, 5, 10 };

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RefreshSwitcher", "settings.ini"); }
        }

        // Reduces user input or a file path to a bare process name: no folder, no ".exe", no characters that
        // could break the ini format. Returns an empty string for anything unusable.
        public static string CleanName(string raw)
        {
            string name = (raw ?? "").Trim().Trim('"').Trim();
            int slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) name = name.Substring(slash + 1);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            if (name.IndexOfAny(BadChars) >= 0 || name.Any(char.IsControl)) return "";
            return name.Length > 100 ? "" : name;
        }

        static readonly char[] BadChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|', ';', '=' };

        // Splits a semicolon separated list and drops empty entries.
        static List<string> Split(string v)
        {
            return v.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        static bool InRange(int v, int min, int max) { return v >= min && v <= max; }

        // Parses settings lines. Pure and testable: invalid or out-of-range values are logged and the default is kept,
        // so a hand-edited or damaged file can never crash the app.
        public static Settings ParseLines(IEnumerable<string> lines)
        {
            var s = new Settings();
            int lineNo = 0;
            foreach (var raw in lines)
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith("#")) continue;
                int i = raw.IndexOf('=');
                if (i <= 0) { Log.Write("Settings line " + lineNo + " ignored: no key"); continue; }
                string k = raw.Substring(0, i).Trim();
                string v = raw.Substring(i + 1).Trim();
                int n;
                bool ok = true;
                if (k == "mode") { SwitchMode m; ok = Enum.TryParse(v, out m) && Enum.IsDefined(typeof(SwitchMode), m); if (ok) s.Mode = m; }
                else if (k == "games") s.Games = Split(v).Select(CleanName).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList();
                else if (k == "ignore") s.FullscreenIgnore = Split(v).Select(CleanName).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList();
                else if (k == "fullscreen") s.DetectFullscreen = v == "1";
                else if (k == "libraries") s.DetectLibraries = v == "1";
                else if (k == "delay") { ok = int.TryParse(v, out n) && InRange(n, 0, 600); if (ok) s.IdleDelay = n; }
                else if (k == "fsdelay") { ok = int.TryParse(v, out n) && InRange(n, 0, 60); if (ok) s.FullscreenDelay = n; }
                else if (k == "poll") { ok = int.TryParse(v, out n) && InRange(n, 1, 10); if (ok) s.PollSeconds = n; }
                else if (k == "tray") s.StartInTray = v == "1";
                else if (k == "battery") s.BatteryMode = v == "1";
                else if (k == "theme")
                {
                    // Map themes removed in earlier versions to the closest current theme
                    if (v.Equals("Midnight", StringComparison.OrdinalIgnoreCase)) v = "Ocean";
                    else if (v.Equals("Plum", StringComparison.OrdinalIgnoreCase) || v.Equals("Neon", StringComparison.OrdinalIgnoreCase)) v = "Graphite";
                    ThemeId th; ok = Enum.TryParse(v, true, out th) && Enum.IsDefined(typeof(ThemeId), th); if (ok) s.Theme = th;
                }
                else if (k.StartsWith("battery.")) { ok = int.TryParse(v, out n) && InRange(n, 1, 1000); if (ok) s.BatteryHz[k.Substring(8)] = n; }
                else if (k == "lang") { Lang l; ok = Enum.TryParse(v, true, out l) && Enum.IsDefined(typeof(Lang), l); if (ok) s.Language = l; }
                else if (k.StartsWith("idle.")) { ok = int.TryParse(v, out n) && InRange(n, 1, 1000); if (ok) s.IdleHz[k.Substring(5)] = n; }
                else if (k.StartsWith("max.")) { ok = int.TryParse(v, out n) && InRange(n, 1, 1000); if (ok) s.MaxHz[k.Substring(4)] = n; }
                else if (k.StartsWith("exclude.")) { if (v == "1") s.ExcludedMonitors.Add(k.Substring(8)); }
                else ok = false;
                if (!ok) Log.Write("Settings line " + lineNo + " ignored: invalid value for '" + k + "'");
            }
            return s;
        }

        // Loads settings from disk; falls back to defaults if the file is missing or unreadable.
        public static Settings Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    Log.Write("No settings file yet, using defaults");
                    return new Settings();
                }
                return ParseLines(File.ReadAllLines(FilePath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Log.Write("Reading settings failed, using defaults: " + ex.Message);
                return new Settings();
            }
        }

        // Writes all settings. Returns false (and logs) on failure instead of throwing.
        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var sb = new StringBuilder();
                sb.AppendLine("mode=" + Mode);
                sb.AppendLine("games=" + string.Join(";", Games));
                sb.AppendLine("ignore=" + string.Join(";", FullscreenIgnore));
                sb.AppendLine("fullscreen=" + (DetectFullscreen ? "1" : "0"));
                sb.AppendLine("libraries=" + (DetectLibraries ? "1" : "0"));
                sb.AppendLine("delay=" + IdleDelay);
                sb.AppendLine("fsdelay=" + FullscreenDelay);
                sb.AppendLine("poll=" + PollSeconds);
                sb.AppendLine("tray=" + (StartInTray ? "1" : "0"));
                sb.AppendLine("battery=" + (BatteryMode ? "1" : "0"));
                sb.AppendLine("theme=" + Theme);
                sb.AppendLine("lang=" + Language);
                foreach (var kv in IdleHz) sb.AppendLine("idle." + kv.Key + "=" + kv.Value);
                foreach (var kv in MaxHz) sb.AppendLine("max." + kv.Key + "=" + kv.Value);
                foreach (var kv in BatteryHz) sb.AppendLine("battery." + kv.Key + "=" + kv.Value);
                foreach (var key in ExcludedMonitors) sb.AppendLine("exclude." + key + "=1");
                // Write to a temp file first, then replace: a crash can never leave a half-written settings.ini
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Saving settings failed: " + ex.Message);
                return false;
            }
        }

        // Keys of saved per-monitor profiles whose monitor is not connected right now.
        public List<string> StaleProfiles(IEnumerable<string> connectedKeys)
        {
            var connected = new HashSet<string>(connectedKeys, StringComparer.OrdinalIgnoreCase);
            return IdleHz.Keys.Concat(MaxHz.Keys).Concat(BatteryHz.Keys).Concat(ExcludedMonitors)
                .Where(k => !connected.Contains(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Deletes all per-monitor values for the given monitor keys.
        public void RemoveProfiles(IEnumerable<string> keys)
        {
            foreach (var k in keys.ToList())
            {
                IdleHz.Remove(k);
                MaxHz.Remove(k);
                BatteryHz.Remove(k);
                ExcludedMonitors.Remove(k);
            }
        }
    }

    // Start-with-Windows entry in HKCU\...\Run. Per user, needs no admin rights.
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "RefreshSwitcher";

        public static bool Enabled
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(Name) != null; }
                catch (Exception ex) { Log.Write("Reading autostart failed: " + ex.Message); return false; }
            }
        }

        // Turns autostart on or off and returns the state actually found in the registry afterwards.
        public static bool Set(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(Name, false);
                }
            }
            catch (Exception ex) { Log.Write("Changing autostart failed: " + ex.Message); }
            bool actual = Enabled;
            Log.Write("Autostart " + (actual ? "on" : "off") + (actual != on ? " (requested " + (on ? "on" : "off") + ")" : ""));
            return actual;
        }
    }

    // ------------------------------------------------------------------ Decision logic without Windows calls (testable)

    // What the monitors should run at: nothing decided yet, desktop rate, game rate or battery rate.
    enum Target { None, Idle, Max, Battery }
    // Why a game was detected; shown in the status line and used for the "Ignore" button.
    enum Reason { None, GameList, Fullscreen, Library }

    // Pure helpers for choosing refresh rates. No Windows calls, covered by Tests.cs.
    static class RateLogic
    {
        // The available rate closest to v; on a tie the higher rate wins.
        public static int Nearest(int[] rates, int v)
        {
            if (rates == null || rates.Length == 0) return 0;
            return rates.OrderBy(r => Math.Abs(r - v)).ThenByDescending(r => r).First();
        }

        // Default desktop rate: 60 Hz if available, otherwise the next higher rate, otherwise the highest.
        public static int DefaultIdle(int[] rates)
        {
            if (rates == null || rates.Length == 0) return 0;
            if (rates.Contains(60)) return 60;
            var above = rates.Where(r => r >= 60).OrderBy(r => r).ToArray();
            return above.Length > 0 ? above[0] : rates.Max();
        }

        // Desktop (or battery) rate: the stored value rounded to an available rate, or the default.
        public static int ResolveIdle(int[] rates, int? stored)
        {
            return stored.HasValue ? Nearest(rates, stored.Value) : DefaultIdle(rates);
        }

        // Game rate: the stored value rounded to an available rate, or the highest rate.
        public static int ResolveMax(int[] rates, int? stored)
        {
            if (rates == null || rates.Length == 0) return 0;
            return stored.HasValue ? Nearest(rates, stored.Value) : rates.Max();
        }

        // True if both lists describe the same monitors with the same resolution and the same available rates.
        // Used to decide whether the monitor rows must be rebuilt after a display change.
        public static bool SameLayout(IList<MonitorInfo> a, IList<MonitorInfo> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i].Key, b[i].Key, StringComparison.OrdinalIgnoreCase)) return false;
                if (a[i].Width != b[i].Width || a[i].Height != b[i].Height) return false;
                if (!a[i].Rates.SequenceEqual(b[i].Rates)) return false;
            }
            return true;
        }
    }

    // Power source information from Windows (battery present, running on battery).
    static class Power
    {
        public static bool HasBattery
        {
            get
            {
                var st = SystemInformation.PowerStatus.BatteryChargeStatus;
                return (st & BatteryChargeStatus.NoSystemBattery) == 0 && st != BatteryChargeStatus.Unknown;
            }
        }

        public static bool OnBattery
        {
            get { return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline; }
        }
    }

    // Decides the desired state (desktop, game or battery) from the mode, the power source,
    // detected games and time. Pure and testable: the current time is passed in.
    // Rules: fixed modes always win; battery wins in automatic mode; listed and library games switch at once;
    // fullscreen apps must stay fullscreen for StabilitySec; after a game the desktop rate returns after IdleDelaySec.
    class SwitchPolicy
    {
        public SwitchMode Mode = SwitchMode.Auto;
        public int IdleDelaySec = 6;   // delay before returning to the desktop rate after a game
        public int StabilitySec = 3;   // how long a fullscreen app must stay before it counts as a game

        public Target Desired { get; private set; }          // target decided by the last Update
        public string ActiveGame { get; private set; }       // process name of the detected game, null if none
        public Reason ActiveReason { get; private set; }     // how the game was detected
        public string PendingCandidate { get; private set; } // fullscreen app still waiting for StabilitySec

        DateTime lastGame = DateTime.MinValue; // last time a game was seen running
        DateTime fsSince;                      // when the current fullscreen candidate appeared

        public double SecondsSinceGame(DateTime now) { return (now - lastGame).TotalSeconds; }

        // Seconds until a pending fullscreen candidate counts as a game.
        public double PendingSecondsLeft(DateTime now)
        {
            return PendingCandidate == null ? 0 : Math.Max(0, StabilitySec - (now - fsSince).TotalSeconds);
        }

        // Convenience overload: plugged in, game from the user's list.
        public Target Update(DateTime now, string listGame, string fullscreenApp)
        {
            return Update(now, listGame, fullscreenApp, false);
        }

        // Convenience overload: game from the user's list.
        public Target Update(DateTime now, string listGame, string fullscreenApp, bool onBattery)
        {
            return Update(now, listGame, fullscreenApp, onBattery, Reason.GameList);
        }

        // onBattery: battery mode is enabled and the device currently runs on battery
        // listReason: where listGame came from (user list or game library); both switch immediately
        public Target Update(DateTime now, string listGame, string fullscreenApp, bool onBattery, Reason listReason)
        {
            if (Mode == SwitchMode.Max || Mode == SwitchMode.Idle)
            {
                Desired = Mode == SwitchMode.Max ? Target.Max : Target.Idle;
                ActiveGame = null;
                ActiveReason = Reason.None;
                PendingCandidate = null;
                return Desired;
            }

            // In automatic mode battery wins, even over a running game
            if (onBattery)
            {
                Desired = Target.Battery;
                ActiveGame = null;
                ActiveReason = Reason.None;
                PendingCandidate = null;
                lastGame = DateTime.MinValue;
                return Desired;
            }

            string game = null;
            Reason reason = Reason.None;

            if (listGame != null)
            {
                // Listed and library games switch immediately, without a stability delay
                game = listGame;
                reason = listReason;
                PendingCandidate = null;
            }
            else if (fullscreenApp != null)
            {
                if (!string.Equals(PendingCandidate, fullscreenApp, StringComparison.OrdinalIgnoreCase))
                {
                    PendingCandidate = fullscreenApp;
                    fsSince = now;
                }
                if ((now - fsSince).TotalSeconds >= StabilitySec)
                {
                    game = fullscreenApp;
                    reason = Reason.Fullscreen;
                }
            }
            else PendingCandidate = null;

            if (game != null)
            {
                ActiveGame = game;
                ActiveReason = reason;
                lastGame = now;
                Desired = Target.Max;
            }
            else if (Desired == Target.None || Desired == Target.Idle || Desired == Target.Battery || SecondsSinceGame(now) >= IdleDelaySec)
            {
                ActiveGame = null;
                ActiveReason = Reason.None;
                Desired = Target.Idle;
            }
            return Desired;
        }

        // Called after the user pressed "Ignore": stop treating this app as a game immediately,
        // without waiting for the back-to-desktop delay.
        public void Forget(string app)
        {
            if (string.Equals(PendingCandidate, app, StringComparison.OrdinalIgnoreCase)) PendingCandidate = null;
            if (string.Equals(ActiveGame, app, StringComparison.OrdinalIgnoreCase))
            {
                ActiveGame = null;
                ActiveReason = Reason.None;
                lastGame = DateTime.MinValue;
            }
        }
    }

    // Retry bookkeeping for failed switches: one attempt plus up to three retries after 5, 15 and 30 seconds.
    // After that it gives up until the target changes or the user changes a setting, so the screen never keeps flickering.
    class RetryState
    {
        public static readonly int[] Backoff = { 5, 15, 30 }; // seconds before retry 1, 2 and 3

        public Target AttemptedTarget { get; private set; } // target of the last attempt
        public bool Success { get; private set; }           // whether the last attempt reached the target on all monitors
        public int Attempts { get; private set; }           // attempts for the current target
        public DateTime NextRetry { get; private set; }     // earliest time for the next retry

        public bool GaveUp { get { return !Success && Attempts > Backoff.Length; } }

        public void Reset() { AttemptedTarget = Target.None; Success = false; Attempts = 0; }

        // True if a switch should be attempted now: new target, or a failed target whose next retry is due.
        public bool ShouldApply(Target desired, DateTime now)
        {
            if (desired == Target.None) return false;
            if (desired != AttemptedTarget) return true;
            if (Success) return false;
            return Attempts <= Backoff.Length && now >= NextRetry;
        }

        // Records the outcome of a switch attempt and schedules the next retry after a failure.
        public void Record(Target t, bool success, DateTime now)
        {
            if (t != AttemptedTarget) Attempts = 0;
            AttemptedTarget = t;
            Success = success;
            Attempts++;
            if (!success && Attempts <= Backoff.Length) NextRetry = now.AddSeconds(Backoff[Attempts - 1]);
        }
    }

    // ------------------------------------------------------------------ Theming

    // Available colour themes. Stored by name in settings.ini, so the numbers may change freely.
    enum ThemeId { Oled = 0, Ocean = 1, Forest = 2, Sunset = 3, Graphite = 4, Light = 5 }

    // One colour theme. State colours keep their meaning in every theme: blue = desktop, orange = game,
    // green = battery, red = error. Contrast is checked by Tests.cs.
    class Palette
    {
        public Color Bg, Surface, SurfaceHi, Line, Text, Muted, Idle, Game, Error, Battery;
        public bool IsLight;

        static Color Hex(int v) { return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255); }

        static Palette Make(int bg, int surface, int surfaceHi, int line, int text, int muted,
            int idle, int game, int error, int battery, bool light)
        {
            return new Palette
            {
                Bg = Hex(bg), Surface = Hex(surface), SurfaceHi = Hex(surfaceHi), Line = Hex(line),
                Text = Hex(text), Muted = Hex(muted), Idle = Hex(idle), Game = Hex(game),
                Error = Hex(error), Battery = Hex(battery), IsLight = light
            };
        }

        // Colour values of a theme.
        public static Palette Get(ThemeId id)
        {
            switch (id)
            {
                case ThemeId.Ocean:
                    return Make(0x06263A, 0x0B3550, 0x14496B, 0x0F3D5A, 0xE8F6FF, 0x9CC3DA, 0x7FD1FF, 0xFFB547, 0xFF7B7B, 0x6EE7A8, false);
                case ThemeId.Forest:
                    return Make(0x0F2A1D, 0x163A28, 0x215036, 0x1A4430, 0xEAF7EE, 0x9CC4AA, 0x74C0FF, 0xFFB547, 0xFF7B7B, 0x7DF0A6, false);
                case ThemeId.Sunset:
                    return Make(0x2E1420, 0x41192B, 0x5A2440, 0x4A1D34, 0xFFEDE6, 0xD4A8B3, 0x7CC4FF, 0xFFB547, 0xFF8F8F, 0x6FE3A1, false);
                case ThemeId.Graphite:
                    return Make(0x16181D, 0x1F2229, 0x2C313B, 0x262A32, 0xE6E8EC, 0x8B93A1, 0x5AA9E6, 0xF2A541, 0xEF5B5B, 0x5BC489, false);
                case ThemeId.Light:
                    return Make(0xF4F5F7, 0xFFFFFF, 0xE3E6EB, 0xDADDE3, 0x1B1D22, 0x5B6270, 0x2A74B4, 0xB06A0A, 0xC23B3B, 0x26874F, true);
                default:
                    return Make(0x000000, 0x111214, 0x1E2024, 0x17181B, 0xEDEEF0, 0x8A8F98, 0x5AA9E6, 0xF2A541, 0xEF5B5B, 0x5BC489, false);
            }
        }

        // WCAG 2.x contrast ratio between two colours (1 to 21). Used by the tests to keep text readable.
        public static double Contrast(Color a, Color b)
        {
            double la = Lum(a), lb = Lum(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        // Relative luminance as defined by WCAG.
        static double Lum(Color c)
        {
            Func<int, double> ch = v => { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); };
            return 0.2126 * ch(c.R) + 0.7152 * ch(c.G) + 0.0722 * ch(c.B);
        }
    }

    // Current colours, fonts and DPI scale used by all custom controls. Everything is drawn by the app itself,
    // so sizes are multiplied by Scale (S / SF) to look the same at 100 %, 150 % or 200 % Windows scaling.
    static class Theme
    {
        // Current colours, set from the palette when the theme changes
        public static Color Bg, Surface, SurfaceHi, Line, Text, Muted, Idle, Game, Error, Battery;
        public static bool IsLight;

        static Theme() { Apply(Palette.Get(ThemeId.Oled)); }

        // Switches all current colours to the given palette. Controls must be rebuilt or repainted afterwards.
        public static void Apply(Palette p)
        {
            Bg = p.Bg; Surface = p.Surface; SurfaceHi = p.SurfaceHi; Line = p.Line;
            Text = p.Text; Muted = p.Muted; Idle = p.Idle; Game = p.Game; Error = p.Error; Battery = p.Battery;
            IsLight = p.IsLight;
        }

        public static float Scale = 1f;
        public static Font Body, BodyBold, Small, Heading, Status, Number, NumberUnit, Mono;


        public static int S(float v) { return (int)Math.Round(v * Scale); }
        public static float SF(float v) { return v * Scale; }

        // Reads the DPI scale and creates fonts. Bahnschrift is used for the large numbers when Windows has it.
        public static void Init()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            Body = new Font("Segoe UI", 9.75f);
            BodyBold = new Font("Segoe UI Semibold", 9.75f);
            Small = new Font("Segoe UI", 8.25f);
            Heading = new Font("Segoe UI Semibold", 11.5f);
            Status = new Font("Segoe UI Semibold", 16f);
            string numFamily = Has("Bahnschrift SemiBold") ? "Bahnschrift SemiBold" : Has("Bahnschrift") ? "Bahnschrift" : "Segoe UI Light";
            Number = new Font(numFamily, 28f);
            NumberUnit = new Font(Has("Bahnschrift") ? "Bahnschrift" : "Segoe UI", 11f);
            Mono = new Font(Has("Cascadia Mono") ? "Cascadia Mono" : "Consolas", 9f);
        }

        // True if a font family is installed.
        static bool Has(string family)
        {
            using (var fc = new InstalledFontCollection())
                return fc.Families.Any(f => f.Name.Equals(family, StringComparison.OrdinalIgnoreCase));
        }

        // Rounded rectangle path for buttons, cards and fields.
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            if (d > r.Height) d = r.Height;
            if (d > r.Width) d = r.Width;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Draws the app/tray icon (stair symbol on a coloured square) in any colour and size.
    static class IconFactory
    {
        // Creates an icon; the tray uses blue, orange or green to show the current state.
        public static Icon Make(Color fill, int size)
        {
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float m = size * 0.06f;
                using (var path = Theme.Round(new RectangleF(m, m, size - 2 * m, size - 2 * m), size * 0.22f))
                using (var b = new SolidBrush(fill))
                    g.FillPath(b, path);
                var pts = new[] { new PointF(.24f, .70f), new PointF(.42f, .70f), new PointF(.42f, .50f), new PointF(.60f, .50f), new PointF(.60f, .30f), new PointF(.78f, .30f) }
                    .Select(p => new PointF(p.X * size, p.Y * size)).ToArray();
                using (var pen = new Pen(Color.FromArgb(0x16, 0x18, 0x1D), Math.Max(1.5f, size * 0.11f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(pen, pts);
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }
    }

    // Colour table that makes Windows context menus follow the current theme.
    class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
        public override Color MenuBorder { get { return Theme.SurfaceHi; } }
        public override Color MenuItemBorder { get { return Theme.SurfaceHi; } }
        public override Color MenuItemSelected { get { return Theme.SurfaceHi; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
        public override Color CheckBackground { get { return Theme.Surface; } }
        public override Color CheckSelectedBackground { get { return Theme.SurfaceHi; } }
        public override Color CheckPressedBackground { get { return Theme.SurfaceHi; } }
    }

    // Context menu renderer: themed text, a dot instead of the black check mark, thin separators.
    class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float d = Theme.SF(6);
            using (var b = new SolidBrush(Theme.Text))
                g.FillEllipse(b, r.X + (r.Width - d) / 2f, r.Y + (r.Height - d) / 2f, d, d);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Theme.SurfaceHi))
                e.Graphics.DrawLine(p, Theme.S(6), y, e.Item.Width - Theme.S(6), y);
        }
    }

    // ------------------------------------------------------------------ Custom controls

    // Base class for all self-drawn controls: double buffered, theme colours, hover tracking.
    // Native WinForms controls cannot be fully recoloured, which is why most of the UI is custom drawn.
    class SkinControl : Control
    {
        protected bool Hover;
        protected const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine;

        public SkinControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
        }

        protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover = false; Invalidate(); base.OnMouseLeave(e); }

        // Turns on anti-aliasing for shapes.
        protected static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }
    }

    // Text without the inner padding of WinForms labels, so it lines up with the custom drawn elements.
    class TextBlock : SkinControl
    {
        public Color TextColor;
        public bool AlignRight, SingleLine;
        public const TextFormatFlags OneLine = TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine;
        public const TextFormatFlags Wrap = TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;

        public TextBlock(string text, Font font, Color color)
        {
            Text = text;
            Font = font;
            TextColor = color;
        }

        // Height the text needs at the given width, including line breaks.
        public int MeasureHeight(int width)
        {
            return TextRenderer.MeasureText(Text ?? "", Font, new Size(Math.Max(1, width), int.MaxValue), Wrap).Height + Theme.S(2);
        }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            var flags = SingleLine ? OneLine : Wrap;
            if (AlignRight) flags = (flags & ~TextFormatFlags.Left) | TextFormatFlags.Right;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, TextColor, flags);
        }
    }

    // Flat rounded button with hover highlight.
    class FlatButton : SkinControl
    {
        public FlatButton(string text)
        {
            Text = text;
            Cursor = Cursors.Hand;
            Height = Theme.S(32);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Smooth(g);
            using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.SF(6)))
            using (var b = new SolidBrush(Hover ? Theme.SurfaceHi : Theme.Surface))
                g.FillPath(b, path);
            TextRenderer.DrawText(g, Text, Theme.Body, ClientRectangle, Enabled ? Theme.Text : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    // Segmented control (a row of mutually exclusive options), used for mode, theme and language.
    class Segmented : SkinControl
    {
        readonly string[] items;
        int selected, hoverIdx = -1;
        public event EventHandler SelectedChanged;

        public Segmented(params string[] items)
        {
            this.items = items;
            Height = Theme.S(36);
            Cursor = Cursors.Hand;
        }

        public int Selected
        {
            get { return selected; }
            set { if (selected != value) { selected = value; Invalidate(); } }
        }

        // Rectangle of segment i.
        RectangleF Seg(int i)
        {
            float pad = Theme.SF(4);
            float w = (Width - 2 * pad) / items.Length;
            return new RectangleF(pad + i * w, pad, w, Height - 2 * pad);
        }

        // Index of the segment under a point, or -1.
        int IndexAt(Point p)
        {
            for (int i = 0; i < items.Length; i++) if (Seg(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i != hoverIdx) { hoverIdx = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverIdx = -1; base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i >= 0 && i != selected)
            {
                Selected = i;
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Smooth(g);
            using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.SF(8)))
            using (var b = new SolidBrush(Theme.Surface))
                g.FillPath(b, p);
            for (int i = 0; i < items.Length; i++)
            {
                var r = Seg(i);
                if (i == selected)
                    using (var p = Theme.Round(r, Theme.SF(6)))
                    using (var b = new SolidBrush(Theme.SurfaceHi))
                        g.FillPath(b, p);
                TextRenderer.DrawText(g, items[i], i == selected ? Theme.BodyBold : Theme.Body, Rectangle.Round(r),
                    i == selected || i == hoverIdx ? Theme.Text : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // On/off switch with a label.
    class Toggle : SkinControl
    {
        bool on;
        public event EventHandler CheckedChanged;

        public Toggle(string text)
        {
            Text = text;
            Height = Theme.S(32);
            Cursor = Cursors.Hand;
        }

        public bool Checked
        {
            get { return on; }
            set { if (on != value) { on = value; Invalidate(); } }
        }

        protected override void OnClick(EventArgs e)
        {
            Checked = !Checked;
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Smooth(g);
            float tw = Theme.SF(36), th = Theme.SF(20), ty = (Height - th) / 2f;
            var track = new RectangleF(0.5f, ty, tw, th);
            using (var p = Theme.Round(track, th / 2))
            using (var b = new SolidBrush(on ? Theme.Text : (Hover ? Theme.SurfaceHi : Theme.Surface)))
                g.FillPath(b, p);
            float kd = Theme.SF(14);
            float kx = on ? track.Right - kd - Theme.SF(3) : track.X + Theme.SF(3);
            using (var b = new SolidBrush(on ? Theme.Bg : Theme.Muted))
                g.FillEllipse(b, kx, ty + (th - kd) / 2f, kd, kd);
            TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(Theme.S(50), 0, Width - Theme.S(50), Height), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    // Value picker with left/right arrows, used for refresh rates and delays.
    // Changes by click on an arrow or by mouse wheel; only values from the given list can be chosen.
    class Stepper : SkinControl
    {
        int[] values = new int[0];
        int index = -1, hoverSide;
        readonly string caption, unit;
        readonly Color mark;
        public event EventHandler ValueChanged;

        int BoxTop { get { return Theme.S(20); } }

        public Stepper(string caption, string unit, Color mark, int width)
        {
            this.caption = caption;
            this.unit = unit;
            this.mark = mark;
            Size = new Size(width, Theme.S(56));
        }

        // Sets the allowed values and selects the one closest to current.
        public void SetValues(int[] vals, int current)
        {
            values = vals ?? new int[0];
            index = Array.IndexOf(values, current);
            if (index < 0 && values.Length > 0)
            {
                index = 0;
                for (int i = 1; i < values.Length; i++)
                    if (Math.Abs(values[i] - current) < Math.Abs(values[index] - current)) index = i;
            }
            Invalidate();
        }

        public int Value { get { return index >= 0 && index < values.Length ? values[index] : 0; } }

        // Moves the selection by d steps and raises ValueChanged.
        void Step(int d)
        {
            int n = index + d;
            if (n < 0 || n >= values.Length) return;
            index = n;
            Invalidate();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int side = 0;
            if (e.Y >= BoxTop)
            {
                if (e.X < Theme.S(34)) side = -1;
                else if (e.X > Width - Theme.S(34)) side = 1;
            }
            if (side != hoverSide) { hoverSide = side; Cursor = side != 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverSide = 0; base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && hoverSide != 0) Step(hoverSide);
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Step(e.Delta > 0 ? 1 : -1);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }

        // Draws a small arrow head.
        void Chevron(Graphics g, float cx, float cy, bool left, Color c)
        {
            float s = Theme.SF(4.5f);
            var pts = left
                ? new[] { new PointF(cx + s / 2, cy - s), new PointF(cx - s / 2, cy), new PointF(cx + s / 2, cy + s) }
                : new[] { new PointF(cx - s / 2, cy - s), new PointF(cx + s / 2, cy), new PointF(cx - s / 2, cy + s) };
            using (var p = new Pen(c, Theme.SF(1.7f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(p, pts);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Smooth(g);
            int cx = 0;
            if (!mark.IsEmpty)
            {
                float d = Theme.SF(7);
                using (var b = new SolidBrush(mark)) g.FillEllipse(b, 0, Theme.SF(5), d, d);
                cx = Theme.S(12);
            }
            TextRenderer.DrawText(g, caption, Theme.Small, new Point(cx, 0), Theme.Muted, Flags);

            var box = new RectangleF(0.5f, BoxTop + 0.5f, Width - 1, Height - BoxTop - 1);
            using (var p = Theme.Round(box, Theme.SF(6)))
            using (var b = new SolidBrush(Theme.Surface))
                g.FillPath(b, p);

            float cy = box.Y + box.Height / 2f;
            Color left = index <= 0 ? Theme.SurfaceHi : hoverSide == -1 ? Theme.Text : Theme.Muted;
            Color right = index >= values.Length - 1 ? Theme.SurfaceHi : hoverSide == 1 ? Theme.Text : Theme.Muted;
            Chevron(g, box.X + Theme.SF(16), cy, true, left);
            Chevron(g, box.Right - Theme.SF(16), cy, false, right);

            string v = values.Length == 0 ? "keine" : Value + " " + unit;
            TextRenderer.DrawText(g, v, Theme.BodyBold, Rectangle.Round(box), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // Status line at the top: coloured dot, state title and a smaller explanation.
    class StatusHeader : SkinControl
    {
        public string Title = "Desktop";
        public string Sub = "";
        public Color Dot = Theme.Idle;
        public Color SubColor = Theme.Muted;

        public StatusHeader() { Height = Theme.S(60); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Smooth(g);
            var ts = TextRenderer.MeasureText(g, Title, Theme.Status, Size.Empty, Flags);
            float d = Theme.SF(11);
            using (var b = new SolidBrush(Dot)) g.FillEllipse(b, 0, ts.Height / 2f - d / 2f + Theme.SF(1), d, d);
            TextRenderer.DrawText(g, Title, Theme.Status, new Point(Theme.S(22), 0), Theme.Text, Flags);
            TextRenderer.DrawText(g, Sub, Theme.Body, new Rectangle(Theme.S(22), ts.Height + Theme.S(4), Width - Theme.S(22), Theme.S(22)),
                SubColor, Flags | TextFormatFlags.EndEllipsis);
        }
    }

    // One monitor: name, active refresh rate (large number), rate pickers for desktop, game and battery,
    // an include switch and a ladder of all available rates with the chosen targets marked as rings.
    class MonitorRow : SkinControl
    {
        public MonitorInfo Info;
        public readonly Stepper IdleStep, MaxStep, BatteryStep;
        public readonly Toggle IncludeToggle;
        public readonly bool ShowBattery;
        public bool GameActive, BatteryActive, Excluded;
        public int IdleTarget, MaxTarget, BatteryTarget;
        public string Error;

        int StepTop { get { return Theme.S(96); } }
        int ToggleW { get { return Math.Min(Width / 2, Theme.S(260)); } }

        public MonitorRow(MonitorInfo info, bool showBattery)
        {
            Info = info;
            ShowBattery = showBattery;
            Height = Theme.S(214);
            IdleStep = new Stepper(L.T("step.desktop"), "Hz", Theme.Idle, Theme.S(136));
            MaxStep = new Stepper(L.T("step.game"), "Hz", Theme.Game, Theme.S(136));
            BatteryStep = new Stepper(L.T("step.battery"), "Hz", Theme.Battery, Theme.S(136)) { Visible = showBattery };
            IncludeToggle = new Toggle(L.T("mon.include"));
            Controls.Add(IdleStep);
            Controls.Add(MaxStep);
            Controls.Add(BatteryStep);
            Controls.Add(IncludeToggle);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (IdleStep == null || MaxStep == null || BatteryStep == null || IncludeToggle == null) return; // the base constructor triggers a layout before the child controls exist
            int gap = Theme.S(10);
            IdleStep.Location = new Point(0, StepTop);
            MaxStep.Location = new Point(IdleStep.Right + gap, StepTop);
            BatteryStep.Location = new Point(MaxStep.Right + gap, StepTop);
            IncludeToggle.SetBounds(Width - ToggleW, Theme.S(10), ToggleW, Theme.S(30));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Color state = Excluded ? Theme.Muted : BatteryActive ? Theme.Battery : GameActive ? Theme.Game : Theme.Idle;

            int leftW = Width - ToggleW - Theme.S(16);
            TextRenderer.DrawText(g, Info.FriendlyName, Theme.BodyBold, new Rectangle(0, Theme.S(12), leftW, Theme.S(22)), Theme.Text, Flags | TextFormatFlags.EndEllipsis);

            // Actual state: the refresh rate that is really active
            string hz = Info.ActiveHz > 0 ? Info.ActiveHz.ToString() : "?";
            int ny = Theme.S(34);
            var ns = TextRenderer.MeasureText(g, hz, Theme.Number, Size.Empty, Flags);
            TextRenderer.DrawText(g, hz, Theme.Number, new Point(-Theme.S(2), ny), state, Flags);
            var us = TextRenderer.MeasureText(g, "Hz", Theme.NumberUnit, Size.Empty, Flags);
            int uy = ny + ns.Height - us.Height - Theme.S(6);
            TextRenderer.DrawText(g, "Hz", Theme.NumberUnit, new Point(ns.Width + Theme.S(4), uy), Theme.Muted, Flags);
            string res = Info.Width > 0 ? Info.Width + " × " + Info.Height : "";
            TextRenderer.DrawText(g, res, Theme.Small, new Point(ns.Width + us.Width + Theme.S(18), uy + Theme.S(3)), Theme.Muted, Flags);

            // Error state, right-aligned below the include switch
            if (!string.IsNullOrEmpty(Error))
                TextRenderer.DrawText(g, Error, Theme.Small, new Rectangle(Width / 2, Theme.S(46), Width - Width / 2, Theme.S(16)), Theme.Error,
                    (Flags & ~TextFormatFlags.Left) | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);

            // Ladder of all available rates
            Smooth(g);
            int ly = Height - Theme.S(34);
            float inset = Theme.SF(17); // outermost ring radius plus line width, so rings at the ends are not clipped
            float x0 = inset, x1 = Width - inset;
            using (var pen = new Pen(Theme.Line, Theme.SF(2))) g.DrawLine(pen, x0, ly, x1, ly);
            int n = Info.Rates.Length;
            for (int i = 0; i < n; i++)
            {
                int r = Info.Rates[i];
                float x = n == 1 ? (x0 + x1) / 2f : x0 + (x1 - x0) * i / (n - 1);
                bool cur = r == Info.ActiveHz;
                float d = cur ? Theme.SF(10) : Theme.SF(6);
                using (var b = new SolidBrush(cur ? state : Theme.SurfaceHi))
                    g.FillEllipse(b, x - d / 2, ly - d / 2, d, d);

                // Rings mark the targets; if several targets share one rate they are drawn as concentric rings
                var roles = new List<Color>();
                if (r == IdleTarget) roles.Add(Theme.Idle);
                if (r == MaxTarget) roles.Add(Theme.Game);
                if (ShowBattery && r == BatteryTarget) roles.Add(Theme.Battery);
                for (int k = 0; k < roles.Count; k++)
                {
                    Color c = Excluded ? Theme.Muted : roles[k];
                    float rd = Theme.SF(17 + 7 * k);
                    using (var p = new Pen(c, Theme.SF(1.5f))) g.DrawEllipse(p, x - rd / 2, ly - rd / 2, rd, rd);
                }
                if (roles.Count > 0)
                {
                    string t = r.ToString();
                    var ts = TextRenderer.MeasureText(g, t, Theme.Small, Size.Empty, Flags);
                    int tx = (int)Math.Max(0, Math.Min(Width - ts.Width, x - ts.Width / 2f));
                    TextRenderer.DrawText(g, t, Theme.Small, new Point(tx, ly + Theme.S(12 + 3 * (roles.Count - 1))), Excluded ? Theme.Muted : roles[0], Flags);
                }
            }
            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }

    // Self-drawn list of game names. Scrolls with the mouse wheel and shows a thin position marker instead of
    // a scroll bar. Hovering a row reveals an X to remove it.
    class GameListView : SkinControl
    {
        List<string> items = new List<string>();
        int selected = -1, hover = -1, offset;
        public Action<string> RemoveRequested;

        int ItemH { get { return Theme.S(32); } }
        int ContentH { get { return items.Count * ItemH; } }
        public bool CanScroll { get { return ContentH > Height; } }

        public GameListView()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        public int SelectedIndex { get { return selected; } }

        // Replaces the list content and keeps selection and scroll position valid.
        public void SetItems(IEnumerable<string> list)
        {
            items = list.ToList();
            if (selected >= items.Count) selected = items.Count - 1;
            ClampOffset();
            Invalidate();
        }

        void ClampOffset() { offset = Math.Max(0, Math.Min(offset, ContentH - Height)); }

        // Row index under a y coordinate, or -1.
        int IndexAt(int y)
        {
            int i = (y + offset) / ItemH;
            return y >= 0 && i >= 0 && i < items.Count ? i : -1;
        }

        // Click area of the remove X in row i.
        Rectangle CloseRect(int i)
        {
            int s = ItemH;
            return new Rectangle(Width - s - Theme.S(6), i * ItemH - offset, s, ItemH);
        }

        // Scrolls just enough to show row i.
        void EnsureVisible(int i)
        {
            if (i < 0) return;
            int top = i * ItemH;
            if (top < offset) offset = top;
            else if (top + ItemH > offset + Height) offset = top + ItemH - Height;
            ClampOffset();
        }

        protected override void OnResize(EventArgs e) { ClampOffset(); base.OnResize(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Y);
            if (i != hover) { hover = i; Invalidate(); }
            Cursor = i >= 0 && CloseRect(i).Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            int i = IndexAt(e.Y);
            if (i >= 0 && CloseRect(i).Contains(e.Location))
            {
                if (RemoveRequested != null) RemoveRequested(items[i]);
                return;
            }
            selected = i;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            offset -= Math.Sign(e.Delta) * ItemH;
            ClampOffset();
            hover = IndexAt(PointToClient(Cursor.Position).Y);
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && selected < items.Count - 1) { selected++; EnsureVisible(selected); Invalidate(); }
            else if (e.KeyCode == Keys.Up && selected > 0) { selected--; EnsureVisible(selected); Invalidate(); }
            else if (e.KeyCode == Keys.Delete && selected >= 0 && RemoveRequested != null) RemoveRequested(items[selected]);
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        // Linear blend between two colours (t = 0..1), used for hover shades.
        static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Smooth(g);
            using (var frame = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Theme.SF(6)))
            {
                using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, frame);
                g.SetClip(frame);

                if (items.Count == 0)
                    TextRenderer.DrawText(g, L.T("games.empty"), Theme.Body, new Point(Theme.S(12), Theme.S(10)), Theme.Muted, Flags);

                for (int i = 0; i < items.Count; i++)
                {
                    int y = i * ItemH - offset;
                    if (y + ItemH < 0 || y > Height) continue;
                    var r = new Rectangle(0, y, Width, ItemH);
                    if (i == selected)
                        using (var b = new SolidBrush(Focused ? Theme.SurfaceHi : Mix(Theme.Surface, Theme.SurfaceHi, 0.6f))) g.FillRectangle(b, r);
                    else if (i == hover)
                        using (var b = new SolidBrush(Mix(Theme.Surface, Theme.SurfaceHi, 0.45f))) g.FillRectangle(b, r);

                    var tr = new Rectangle(Theme.S(12), y, Width - Theme.S(12) - ItemH - Theme.S(8), ItemH);
                    TextRenderer.DrawText(g, items[i] + ".exe", Theme.Body, tr, Theme.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

                    if (i == hover)
                    {
                        var cr = CloseRect(i);
                        bool onX = cr.Contains(PointToClient(Cursor.Position));
                        float cx = cr.X + cr.Width / 2f, cy = cr.Y + cr.Height / 2f, s = Theme.SF(4.5f);
                        using (var p = new Pen(onX ? Theme.Text : Theme.Muted, Theme.SF(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        {
                            g.DrawLine(p, cx - s, cy - s, cx + s, cy + s);
                            g.DrawLine(p, cx - s, cy + s, cx + s, cy - s);
                        }
                    }
                }

                // Thin position marker instead of a scroll bar, only if the list overflows
                if (ContentH > Height)
                {
                    float h = Math.Max(Theme.SF(24), Height * (float)Height / ContentH);
                    float ty = (Height - h) * offset / (float)(ContentH - Height);
                    using (var p = Theme.Round(new RectangleF(Width - Theme.SF(6), ty + Theme.SF(3), Theme.SF(3), h - Theme.SF(6)), Theme.SF(1.5f)))
                    using (var b = new SolidBrush(Color.FromArgb(140, Theme.Muted)))
                        g.FillPath(b, p);
                }
                g.ResetClip();
            }
        }
    }

    // Applies dark or light title bar colours that match the theme. Fails silently on older Windows.
    static class DarkWindow
    {
        public static void Apply(Form f)
        {
            try
            {
                int dark = Theme.IsLight ? 0 : 1;
                if (Native.DwmSetWindowAttribute(f.Handle, 20, ref dark, 4) != 0)
                    Native.DwmSetWindowAttribute(f.Handle, 19, ref dark, 4);
                int caption = Theme.Bg.R | (Theme.Bg.G << 8) | (Theme.Bg.B << 16);
                Native.DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
                int textColor = Theme.Text.R | (Theme.Text.G << 8) | (Theme.Text.B << 16);
                Native.DwmSetWindowAttribute(f.Handle, 36, ref textColor, 4);
            }
            catch { }
        }
    }

    // Scrolling without a visible scroll bar: mouse wheel, dragging with mouse or touch on empty areas,
    // handled as an application-wide message filter so it works no matter which child control is under the cursor.
    // Controls that need the wheel themselves (rate pickers, a long game list) keep it.
    class NoBarScroller : IMessageFilter
    {
        const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEWHEEL = 0x020A;

        readonly Form form;
        readonly Func<int> maxScroll, current;
        readonly Action<int> scrollTo;
        readonly Func<Control, bool> keepsWheel, isDragSurface;
        bool dragging, dragActive;
        int startY, startScroll;

        public NoBarScroller(Form form, Func<int> maxScroll, Func<int> current, Action<int> scrollTo,
            Func<Control, bool> keepsWheel, Func<Control, bool> isDragSurface)
        {
            this.form = form;
            this.maxScroll = maxScroll;
            this.current = current;
            this.scrollTo = scrollTo;
            this.keepsWheel = keepsWheel;
            this.isDragSurface = isDragSurface;
        }

        // Deepest visible child control at a screen point.
        static Control DeepChildAt(Control parent, Point screen)
        {
            var c = parent.GetChildAtPoint(parent.PointToClient(screen), GetChildAtPointSkip.Invisible);
            return c == null ? parent : DeepChildAt(c, screen);
        }

        // True if the message targets this form or one of its children.
        bool Mine(IntPtr hwnd)
        {
            var c = Control.FromHandle(hwnd);
            return c != null && !form.IsDisposed && form.Visible && (c == form || c.FindForm() == form);
        }

        // Intercepts wheel and left-button messages; returns true when the message was used for scrolling.
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL && m.Msg != WM_LBUTTONDOWN && m.Msg != WM_MOUSEMOVE && m.Msg != WM_LBUTTONUP) return false;
            if (!Mine(m.HWnd)) return false;

            if (m.Msg == WM_MOUSEWHEEL)
            {
                if (maxScroll() <= 0) return false;
                var pos = Cursor.Position;
                if (!form.ClientRectangle.Contains(form.PointToClient(pos))) return false;
                if (keepsWheel(DeepChildAt(form, pos))) return false;
                int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                scrollTo(current() - delta * Theme.S(64) / 120);
                return true;
            }
            if (m.Msg == WM_LBUTTONDOWN)
            {
                if (maxScroll() > 0 && isDragSurface(Control.FromHandle(m.HWnd)))
                {
                    dragging = true;
                    dragActive = false;
                    startY = Cursor.Position.Y;
                    startScroll = current();
                }
                return false;
            }
            if (m.Msg == WM_MOUSEMOVE)
            {
                if (!dragging) return false;
                if ((Control.MouseButtons & MouseButtons.Left) == 0) { dragging = dragActive = false; return false; }
                int dy = Cursor.Position.Y - startY;
                if (!dragActive && Math.Abs(dy) > Theme.S(6)) dragActive = true;
                if (dragActive) { scrollTo(startScroll - dy); return true; }
                return false;
            }
            // WM_LBUTTONUP: after a real drag, swallow the click so nothing gets toggled by accident
            bool wasDrag = dragActive;
            dragging = dragActive = false;
            return wasDrag;
        }
    }

    // Main window and tray icon. Owns the polling timer and connects detection, decision logic and switching:
    // Evaluate() -> SwitchPolicy decides the target -> RetryState decides whether to act -> ApplyTarget() switches.
    // The window can be resized freely; the layout reflows into one or two columns and scrolls when needed.
    class MainForm : Form
    {
        readonly Settings settings;                      // user settings, saved after every change
        readonly SwitchPolicy policy = new SwitchPolicy(); // decides the target state
        readonly RetryState retry = new RetryState();      // limits retries after failed switches
        readonly System.Windows.Forms.Timer timer;        // polls for games every PollSeconds
        NotifyIcon tray;
        ContextMenuStrip trayMenu, pickerMenu;
        ToolStripMenuItem miAuto, miMax, miIdle;
        Icon iconIdle, iconGame, iconBattery;
        EventWaitHandle exitSignal;                        // set by "RefreshSwitcher.exe --exit" from the installer
        DiagnosticsForm diag;                              // open diagnostics window, if any

        StatusHeader header;
        Segmented modeSel, langSel, themeSel;
        TextBlock monHeading, monHint, noMonLabel, gamesHeading, gamesHint, behHeading, fsHint, batteryHint, langCaption, themeCaption, libHint, footer, credit;
        readonly List<MonitorRow> rows = new List<MonitorRow>();
        string lastPickDir;                                // last folder used in the "Choose .exe" dialog
        FlatButton addBtn, removeBtn, pickBtn, ignoreBtn, resetIgnoreBtn, diagBtn;
        GameListView gameList;
        Toggle fsToggle, autoToggle, trayToggle, batteryToggle, libToggle;
        List<string> libraryRoots = new List<string>();    // game library folders found on this PC
        DateTime libraryScanTime = DateTime.MinValue;      // last library scan, rescanned every 10 minutes
        Stepper delay, fsDelay, poll;

        bool forceApply;    // apply the target again on the next evaluation even if it did not change
        bool exiting;       // shutting down: ignore timer and display events
        bool balloonShown;  // the "still running in the tray" hint was shown once
        bool startHidden;   // start without showing the window
        bool layoutBusy;    // guards against re-entrant layout passes
        int scrollY, contentHeight;                        // scroll offset and total content height in pixels
        NoBarScroller scroller;                            // wheel/drag scrolling without a scroll bar

        // Error state per monitor: monitor key -> { monitor name, localised error text }
        Dictionary<string, string[]> monitorErrors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        // Last switch, stored language-neutral so the status line can be re-translated
        DateTime? lastSwitchTime;
        Target lastSwitchTarget;
        string lastSwitchGame;

        // Layout sizes in pixels at 100 % scaling (multiplied by Theme.Scale)
        const int Pad = 24, ColGap = 40, NarrowMinW = 600, WideContent = 1000, MaxContent = 1400, MinList = 116, PrefList = 170;

        // Builds the window. hidden = start in the notification area only.
        public MainForm(Settings s, bool hidden)
        {
            settings = s;
            startHidden = hidden;

            Text = "RefreshSwitcher";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            AutoScroll = false;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen;

            MakeTrayIcons();
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { Icon = IconFactory.Make(Theme.Idle, 32); }

            SyncPolicy();
            if (settings.DetectLibraries) RescanLibraries();
            Log.Write("Creating tray icon");
            BuildTray();
            Log.Write("Creating controls");
            BuildControls();
            Log.Write("Reading monitors");
            BuildRows();
            Log.Write("Sizing window");
            InitialSize();

            timer = new System.Windows.Forms.Timer { Interval = settings.PollSeconds * 1000 };
            timer.Tick += (a, b) => Evaluate();
            timer.Start();

            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SystemEvents.PowerModeChanged += OnPowerChanged;

            scroller = new NoBarScroller(this, () => MaxScroll, () => scrollY, ScrollTo,
                c => c is Stepper || (c is GameListView && ((GameListView)c).CanScroll),
                c => c == this || c is TextBlock || c is StatusHeader || c is MonitorRow);
            Application.AddMessageFilter(scroller);
            Log.Write("Power: battery present " + Power.HasBattery + ", on battery " + Power.OnBattery + ", battery mode " + settings.BatteryMode);

            // The installer and uninstaller can close the app cleanly with "RefreshSwitcher.exe --exit"
            exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
            ThreadPool.RegisterWaitForSingleObject(exitSignal, (st, timedOut) =>
            {
                Log.Write("Exit signal received");
                try { if (IsHandleCreated) BeginInvoke((Action)ExitApp); } catch { }
            }, null, -1, true);

            Log.Write("First evaluation");
            Evaluate();
        }

        // Keeps the window invisible on the very first show when starting hidden, while still creating its handle.
        protected override void SetVisibleCore(bool value)
        {
            if (startHidden)
            {
                startHidden = false;
                if (!IsHandleCreated) CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DarkWindow.Apply(this);
        }

        static int S(float v) { return Theme.S(v); }

        bool BatteryEnabled { get { return settings.BatteryMode; } }

        // Copies the timing and mode settings into the decision logic.
        void SyncPolicy()
        {
            policy.Mode = settings.Mode;
            policy.IdleDelaySec = settings.IdleDelay;
            policy.StabilitySec = settings.FullscreenDelay;
        }

        // ---------- Building the UI

        // Creates the notification area icon. Left click opens the window.
        void BuildTray()
        {
            tray = new NotifyIcon { Icon = iconIdle, Text = "RefreshSwitcher", Visible = true };
            tray.MouseClick += (a, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
            BuildTrayMenu();
        }

        // (Re)creates the tray menu, e.g. after a language change.
        void BuildTrayMenu()
        {
            var old = trayMenu;
            trayMenu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, ShowCheckMargin = true, Font = Theme.Body };
            var open = new ToolStripMenuItem(L.T("tray.open"), null, (a, b) => ShowFromTray()) { Font = Theme.BodyBold };
            miAuto = new ToolStripMenuItem(L.T("mode.auto"), null, (a, b) => SetMode(SwitchMode.Auto));
            miMax = new ToolStripMenuItem(L.T("tray.max"), null, (a, b) => SetMode(SwitchMode.Max));
            miIdle = new ToolStripMenuItem(L.T("tray.idle"), null, (a, b) => SetMode(SwitchMode.Idle));
            var diagItem = new ToolStripMenuItem(L.T("tray.diag"), null, (a, b) => ShowDiagnostics());
            var logItem = new ToolStripMenuItem(L.T("tray.log"), null, (a, b) => Log.Open());
            var exit = new ToolStripMenuItem(L.T("tray.exit"), null, (a, b) => ExitApp());
            trayMenu.Items.AddRange(new ToolStripItem[] { open, new ToolStripSeparator(), miAuto, miMax, miIdle, new ToolStripSeparator(), diagItem, logItem, new ToolStripSeparator(), exit });
            tray.ContextMenuStrip = trayMenu;
            if (old != null) old.Dispose();
        }

        // Creates a text block and adds it to the window.
        TextBlock MakeText(string text, Font font, Color color)
        {
            var t = new TextBlock(text, font, color);
            Controls.Add(t);
            return t;
        }

        // Text below the library switch: how many game folders were found.
        string LibraryHintText()
        {
            return libraryRoots.Count == 0 ? L.T("games.librariesNone") : L.F("games.librariesFound", libraryRoots.Count);
        }

        // Searches all game library folders again and updates the hint if the count changed.
        void RescanLibraries()
        {
            libraryRoots = LibraryScanner.FindRoots();
            libraryScanTime = DateTime.Now;
            Log.Write("Game folders found: " + libraryRoots.Count + (libraryRoots.Count > 0 ? " (" + string.Join(" | ", libraryRoots) + ")" : ""));
            if (libHint != null && libHint.Text != LibraryHintText())
            {
                libHint.Text = LibraryHintText();
                DoLayoutAll();
            }
        }

        // Text below the battery switch describing the current power source.
        string BatteryHintText()
        {
            if (!Power.HasBattery) return L.T("beh.batteryNone");
            return L.T(Power.OnBattery ? "beh.batteryOn" : "beh.batteryAc");
        }

        // Windows power event: re-evaluate immediately on plug/unplug, force a re-apply after resume from sleep.
        void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (!IsHandleCreated || exiting) return;
            Log.Write("Power event " + e.Mode + ": on battery " + Power.OnBattery);
            BeginInvoke((Action)(() =>
            {
                if (e.Mode == PowerModes.Resume) forceApply = true; // after sleep Windows may have reset the display mode, so apply again
                if (batteryHint != null) batteryHint.Text = BatteryHintText();
                Evaluate();
            }));
        }

        // Text listing the apps excluded from automatic detection.
        string IgnoreHintText()
        {
            return settings.FullscreenIgnore.Count == 0
                ? L.T("beh.excludedNone")
                : L.F("beh.excluded", string.Join(", ", settings.FullscreenIgnore));
        }

        // Creates every control of the window (except the monitor rows) with the current language and theme.
        void BuildControls()
        {
            header = new StatusHeader();
            modeSel = new Segmented(L.T("mode.auto"), L.T("mode.max"), L.T("mode.idle"));
            modeSel.Selected = (int)settings.Mode;
            modeSel.SelectedChanged += (a, b) => SetMode((SwitchMode)modeSel.Selected);
            ignoreBtn = new FlatButton("") { Visible = false };
            ignoreBtn.Click += (a, b) => IgnoreCurrentFullscreenApp();
            Controls.AddRange(new Control[] { header, modeSel, ignoreBtn });

            monHeading = MakeText(L.T("mon.heading"), Theme.Heading, Theme.Text);
            monHint = MakeText(L.T(BatteryEnabled ? "mon.hintBattery" : "mon.hint"), Theme.Small, Theme.Muted);
            noMonLabel = MakeText(L.T("mon.none"), Theme.Body, Theme.Muted);

            gamesHeading = MakeText(L.T("games.heading"), Theme.Heading, Theme.Text);
            gamesHint = MakeText(L.T("games.hint"), Theme.Small, Theme.Muted);
            addBtn = new FlatButton(L.T("games.add"));
            addBtn.Click += (a, b) => ChooseExe();
            gameList = new GameListView();
            gameList.RemoveRequested = RemoveGame;
            removeBtn = new FlatButton(L.T("games.remove"));
            removeBtn.Click += (a, b) =>
            {
                int i = gameList.SelectedIndex;
                if (i >= 0 && i < settings.Games.Count) RemoveGame(settings.Games[i]);
            };
            pickBtn = new FlatButton(L.T("games.pick"));
            pickBtn.Click += (a, b) => ShowPicker(pickBtn);
            libToggle = new Toggle(L.T("games.libraries")) { Checked = settings.DetectLibraries };
            libToggle.CheckedChanged += (a, b) =>
            {
                settings.DetectLibraries = libToggle.Checked;
                settings.Save();
                Log.Write("Game library detection " + (settings.DetectLibraries ? "on" : "off"));
                if (settings.DetectLibraries) RescanLibraries();
                Evaluate();
            };
            libHint = MakeText(LibraryHintText(), Theme.Small, Theme.Muted);
            Controls.AddRange(new Control[] { addBtn, gameList, removeBtn, pickBtn, libToggle });

            behHeading = MakeText(L.T("beh.heading"), Theme.Heading, Theme.Text);
            batteryToggle = new Toggle(L.T("beh.battery")) { Checked = settings.BatteryMode };
            batteryToggle.CheckedChanged += (a, b) =>
            {
                settings.BatteryMode = batteryToggle.Checked;
                settings.Save();
                Log.Write("Battery mode " + (settings.BatteryMode ? "on" : "off"));
                monHint.Text = L.T(BatteryEnabled ? "mon.hintBattery" : "mon.hint");
                forceApply = true;
                BuildRows();
                Evaluate();
            };
            batteryHint = MakeText(BatteryHintText(), Theme.Small, Theme.Muted);
            Controls.Add(batteryToggle);
            fsToggle = new Toggle(L.T("beh.fullscreen")) { Checked = settings.DetectFullscreen };
            fsToggle.CheckedChanged += (a, b) => { settings.DetectFullscreen = fsToggle.Checked; settings.Save(); Evaluate(); };
            fsHint = MakeText(IgnoreHintText(), Theme.Small, Theme.Muted);
            resetIgnoreBtn = new FlatButton(L.T("beh.resetIgnore"));
            resetIgnoreBtn.Click += (a, b) =>
            {
                settings.FullscreenIgnore = new List<string>(Settings.DefaultIgnore);
                settings.Save();
                Log.Write("Fullscreen exclusions reset to defaults");
                fsHint.Text = IgnoreHintText();
                UpdateMinimumSize();
                DoLayoutAll();
            };

            delay = new Stepper(L.T("beh.delay"), "s", Color.Empty, S(268));
            delay.SetValues(Settings.DelayChoices, settings.IdleDelay);
            delay.ValueChanged += (a, b) => { settings.IdleDelay = delay.Value; settings.Save(); SyncPolicy(); };
            fsDelay = new Stepper(L.T("beh.fsDelay"), "s", Color.Empty, S(268));
            fsDelay.SetValues(Settings.FullscreenDelayChoices, settings.FullscreenDelay);
            fsDelay.ValueChanged += (a, b) => { settings.FullscreenDelay = fsDelay.Value; settings.Save(); SyncPolicy(); };
            poll = new Stepper(L.T("beh.poll"), "s", Color.Empty, S(268));
            poll.SetValues(Settings.PollChoices, settings.PollSeconds);
            poll.ValueChanged += (a, b) =>
            {
                settings.PollSeconds = poll.Value;
                settings.Save();
                if (timer != null) timer.Interval = settings.PollSeconds * 1000;
            };
            diagBtn = new FlatButton(L.T("beh.diag"));
            diagBtn.Click += (a, b) => ShowDiagnostics();

            autoToggle = new Toggle(L.T("beh.autostart")) { Checked = Autostart.Enabled };
            autoToggle.CheckedChanged += (a, b) =>
            {
                bool actual = Autostart.Set(autoToggle.Checked);
                if (actual != autoToggle.Checked) autoToggle.Checked = actual;
            };
            trayToggle = new Toggle(L.T("beh.tray")) { Checked = settings.StartInTray };
            trayToggle.CheckedChanged += (a, b) => { settings.StartInTray = trayToggle.Checked; settings.Save(); };
            langCaption = MakeText(L.T("beh.language"), Theme.Small, Theme.Muted);
            langSel = new Segmented("English", "Deutsch", "Español", "Français", "Українська");
            langSel.Selected = (int)settings.Language;
            langSel.SelectedChanged += (a, b) =>
            {
                var lang = (Lang)langSel.Selected;
                BeginInvoke((Action)(() => ChangeLanguage(lang)));
            };
            themeCaption = MakeText(L.T("beh.theme"), Theme.Small, Theme.Muted);
            themeSel = new Segmented(L.T("theme.oled"), L.T("theme.ocean"), L.T("theme.forest"), L.T("theme.sunset"), L.T("theme.graphite"), L.T("theme.light"));
            themeSel.Selected = (int)settings.Theme;
            themeSel.SelectedChanged += (a, b) =>
            {
                var th = (ThemeId)themeSel.Selected;
                BeginInvoke((Action)(() => ChangeTheme(th)));
            };
            Controls.AddRange(new Control[] { fsToggle, resetIgnoreBtn, delay, fsDelay, poll, diagBtn, autoToggle, trayToggle, langSel, themeSel });

            footer = new TextBlock(FooterText(), Theme.Body, Theme.Muted);
            credit = new TextBlock("Created by SmokingTux", Theme.Small, Theme.Muted) { AlignRight = true, SingleLine = true };
            Controls.Add(footer);
            Controls.Add(credit);
            RefreshGameList();
        }

        // Switches the UI language and rebuilds all controls. The log stays English.
        void ChangeLanguage(Lang lang)
        {
            if (lang == settings.Language) return;
            Log.Write("Language changed to " + lang);
            settings.Language = lang;
            L.Current = lang;
            settings.Save();

            RebuildUi();
            if (diag != null && !diag.IsDisposed) diag.Retranslate();
        }

        // Switches the colour theme and rebuilds all controls, the title bar and the tray icons.
        void ChangeTheme(ThemeId id)
        {
            if (id == settings.Theme) return;
            Log.Write("Theme changed to " + id);
            settings.Theme = id;
            settings.Save();
            Theme.Apply(Palette.Get(id));

            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            DarkWindow.Apply(this);
            MakeTrayIcons();
            RebuildUi();
            // The diagnostics window picks up the new colours the next time it opens
            if (diag != null && !diag.IsDisposed) diag.Close();
        }

        // Disposes and recreates all controls while keeping the scroll position.
        void RebuildUi()
        {
            int keepScroll = scrollY;
            SuspendLayout();
            foreach (Control c in Controls.Cast<Control>().ToList()) c.Dispose();
            Controls.Clear();
            rows.Clear();
            BuildControls();
            BuildTrayMenu();
            ResumeLayout(false);
            BuildRows();
            scrollY = keepScroll;
            DoLayoutAll();
            Invalidate(true);
        }

        // Creates the three state icons for the tray (desktop, game, battery) in the current theme.
        void MakeTrayIcons()
        {
            int small = Math.Max(16, SystemInformation.SmallIconSize.Width);
            iconIdle = IconFactory.Make(Theme.Idle, small);
            iconGame = IconFactory.Make(Theme.Game, small);
            iconBattery = IconFactory.Make(Theme.Battery, small);
        }

        // (Re)creates one MonitorRow per connected monitor and wires its rate pickers to the settings.
        void BuildRows()
        {
            SuspendLayout();
            foreach (var r in rows) { Controls.Remove(r); r.Dispose(); }
            rows.Clear();

            var monitors = Display.GetMonitors();
            foreach (var mon in monitors)
                Log.Write("Monitor " + mon.GdiName + " | " + mon.FriendlyName + " | " + mon.Width + "x" + mon.Height +
                    " | desktop setting " + mon.RegistryHz + " Hz, active " + mon.ActiveHz + " Hz | rates: " + string.Join(",", mon.Rates));

            foreach (var mon in monitors)
            {
                var m = mon;
                var row = new MonitorRow(m, BatteryEnabled);
                row.IdleTarget = IdleFor(m);
                row.MaxTarget = MaxFor(m);
                row.BatteryTarget = BatteryFor(m);
                row.BatteryStep.SetValues(m.Rates, row.BatteryTarget);
                row.BatteryStep.ValueChanged += (a, b) =>
                {
                    settings.BatteryHz[m.Key] = row.BatteryStep.Value;
                    settings.Save();
                    row.BatteryTarget = row.BatteryStep.Value;
                    row.Invalidate();
                    forceApply = true;
                    Evaluate();
                };
                row.Excluded = settings.ExcludedMonitors.Contains(m.Key);
                row.IncludeToggle.Checked = !row.Excluded;
                row.IdleStep.SetValues(m.Rates, row.IdleTarget);
                row.MaxStep.SetValues(m.Rates, row.MaxTarget);
                row.IdleStep.ValueChanged += (a, b) =>
                {
                    settings.IdleHz[m.Key] = row.IdleStep.Value;
                    settings.Save();
                    row.IdleTarget = row.IdleStep.Value;
                    row.Invalidate();
                    forceApply = true;
                    Evaluate();
                };
                row.MaxStep.ValueChanged += (a, b) =>
                {
                    settings.MaxHz[m.Key] = row.MaxStep.Value;
                    settings.Save();
                    row.MaxTarget = row.MaxStep.Value;
                    row.Invalidate();
                    forceApply = true;
                    Evaluate();
                };
                row.IncludeToggle.CheckedChanged += (a, b) =>
                {
                    if (row.IncludeToggle.Checked) settings.ExcludedMonitors.Remove(m.Key);
                    else settings.ExcludedMonitors.Add(m.Key);
                    settings.Save();
                    row.Excluded = !row.IncludeToggle.Checked;
                    monitorErrors.Remove(m.Key);
                    Log.Write("Monitor " + m.GdiName + (row.Excluded ? " excluded from" : " included in") + " switching");
                    row.Invalidate();
                    forceApply = true;
                    Evaluate();
                };
                rows.Add(row);
                Controls.Add(row);
            }
            noMonLabel.Visible = rows.Count == 0;
            ResumeLayout(false);

            UpdateMinimumSize();
            DoLayoutAll();
            UpdateUi();
        }

        // ---------- Responsive layout without scroll bars

        // Places a control (when apply is true) and advances y by its height plus gap.
        void Put(Control c, int x, ref int y, int w, int h, int gapAfter, bool apply)
        {
            if (apply) c.SetBounds(x, y, w, h);
            y += h + gapAfter;
        }

        // Places a text block with the height its text needs.
        void PutText(TextBlock t, int x, ref int y, int w, int gapAfter, bool apply)
        {
            Put(t, x, ref y, w, t.MeasureHeight(w), gapAfter, apply);
        }

        // Lays out the whole window. wide = two columns (monitors left, games and behaviour right).
        // apply = false only measures. offset shifts everything vertically for scrolling.
        // Returns the bottom of column 1 and column 2.
        int[] Arrange(bool wide, int left, int contentW, int listH, bool apply, int offset)
        {
            int y = S(22) + offset;
            int segW = Math.Min(S(360), contentW / 2);
            if (apply)
            {
                header.SetBounds(left, y, contentW - segW - S(12), header.Height);
                modeSel.SetBounds(left + contentW - segW, y + S(4), segW, modeSel.Height);
                ignoreBtn.SetBounds(left + contentW - segW, y + S(46), segW, S(28));
            }
            y += header.Height + S(22) + S(14);
            int colTop = y;

            int gap = S(ColGap);
            int colW = wide ? (contentW - gap) / 2 : contentW;
            int lx = left, rx = wide ? left + colW + gap : left;

            // Monitors
            PutText(monHeading, lx, ref y, colW, S(2), apply);
            PutText(monHint, lx, ref y, colW, S(4), apply);
            foreach (var r in rows) Put(r, lx, ref y, colW, r.Height, 0, apply);
            if (rows.Count == 0) Put(noMonLabel, lx, ref y, colW, S(24), 0, apply);
            // The status line moves down with every additional monitor
            y += S(14);
            PutText(footer, lx, ref y, colW, 0, apply);
            int leftBottom = y;

            // Games
            int ry = wide ? colTop : y + S(28);
            PutText(gamesHeading, rx, ref ry, colW, S(2), apply);
            PutText(gamesHint, rx, ref ry, colW, S(10), apply);
            int btnW = S(170), bgap = S(10), listW = colW - btnW - bgap, bx = rx + listW + bgap;
            if (apply)
            {
                gameList.SetBounds(rx, ry, listW, listH);
                addBtn.SetBounds(bx, ry, btnW, S(32));
                pickBtn.SetBounds(bx, ry + S(42), btnW, S(32));
                removeBtn.SetBounds(bx, ry + S(84), btnW, S(32));
            }
            ry += Math.Max(listH, S(116)) + S(14);
            Put(libToggle, rx, ref ry, colW, S(32), S(2), apply);
            PutText(libHint, rx + S(50), ref ry, colW - S(50), S(28), apply);

            // Behaviour
            PutText(behHeading, rx, ref ry, colW, S(8), apply);
            Put(batteryToggle, rx, ref ry, colW, S(32), S(2), apply);
            PutText(batteryHint, rx + S(50), ref ry, colW - S(50), S(14), apply);
            Put(fsToggle, rx, ref ry, colW, S(32), S(2), apply);
            PutText(fsHint, rx + S(50), ref ry, colW - S(50), S(6), apply);
            Put(resetIgnoreBtn, rx + S(50), ref ry, Math.Min(S(200), colW - S(50)), S(28), S(16), apply);

            int stepGap = S(12);
            int stepW = Math.Min(S(280), (colW - stepGap) / 2);
            if (apply)
            {
                delay.SetBounds(rx, ry, stepW, delay.Height);
                fsDelay.SetBounds(rx + stepW + stepGap, ry, stepW, fsDelay.Height);
            }
            ry += delay.Height + S(12);
            if (apply)
            {
                poll.SetBounds(rx, ry, stepW, poll.Height);
                diagBtn.SetBounds(rx + stepW + stepGap, ry + S(20), stepW, poll.Height - S(20));
            }
            ry += poll.Height + S(14);

            Put(autoToggle, rx, ref ry, colW, S(32), S(4), apply);
            Put(trayToggle, rx, ref ry, colW, S(32), S(14), apply);
            PutText(themeCaption, rx, ref ry, colW, S(4), apply);
            Put(themeSel, rx, ref ry, Math.Min(colW, S(560)), themeSel.Height, S(14), apply);
            PutText(langCaption, rx, ref ry, colW, S(4), apply);
            Put(langSel, rx, ref ry, Math.Min(colW, S(560)), langSel.Height, 0, apply);

            return new[] { leftBottom, ry };
        }

        int FooterSpace { get { return S(20) + S(18) + S(16); } }

        // Total height the content needs for a given width and list height.
        int RequiredHeight(bool wide, int contentW, int listH)
        {
            var b = Arrange(wide, 0, contentW, listH, false, 0);
            return Math.Max(b[0], b[1]) + FooterSpace;
        }

        // Sets the smallest window size that shows everything: one column if that fits on screen, otherwise two columns.
        // Never taller than the screen; anything that still does not fit can be scrolled.
        void UpdateMinimumSize()
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int narrowW = S(NarrowMinW);
            int narrowH = RequiredHeight(false, narrowW - 2 * S(Pad), S(MinList));
            var narrow = SizeFromClientSize(new Size(narrowW, narrowH));
            if (narrow.Height <= wa.Height)
            {
                MinimumSize = narrow;
                return;
            }
            int wideW = S(WideContent) + 2 * S(Pad);
            int wideH = RequiredHeight(true, S(WideContent), S(MinList));
            var wide = SizeFromClientSize(new Size(wideW, wideH));
            // Even two columns do not fit (high scaling): keep the window screen-high and scroll the rest
            MinimumSize = new Size(Math.Min(wide.Width, wa.Width), Math.Min(wide.Height, wa.Height));
        }

        // Chooses a comfortable start size that fits the screen.
        void InitialSize()
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int narrowW = S(NarrowMinW + 40);
            var narrow = SizeFromClientSize(new Size(narrowW, RequiredHeight(false, narrowW - 2 * S(Pad), S(PrefList))));
            Size target;
            if (narrow.Height <= wa.Height) target = narrow;
            else
            {
                int wideW = S(WideContent + 120) + 2 * S(Pad);
                target = SizeFromClientSize(new Size(wideW, RequiredHeight(true, wideW - 2 * S(Pad), S(PrefList))));
            }
            Size = new Size(Math.Max(MinimumSize.Width, Math.Min(target.Width, wa.Width)), Math.Max(MinimumSize.Height, Math.Min(target.Height, wa.Height)));
            DoLayoutAll();
        }

        // Arranges all controls for the current window size and scroll position.
        void DoLayoutAll()
        {
            if (layoutBusy || header == null || footer == null || credit == null) return;
            layoutBusy = true;
            SuspendLayout();
            try
            {
                int pad = S(Pad);
                int avail = Math.Max(S(200), ClientSize.Width - 2 * pad);
                int contentW = Math.Min(avail, S(MaxContent));
                int left = (ClientSize.Width - contentW) / 2;
                bool wide = contentW >= S(WideContent);

                // The game list grows until its column reaches the bottom of the window
                var b = Arrange(wide, left, contentW, S(MinList), false, 0);
                int targetBottom = ClientSize.Height - FooterSpace;
                int extra = Math.Max(0, targetBottom - b[1]);
                int listH = S(MinList) + extra;
                contentHeight = Math.Max(b[0], b[1] + extra) + FooterSpace;
                scrollY = Math.Max(0, Math.Min(scrollY, MaxScroll));
                Arrange(wide, left, contentW, listH, true, -scrollY);

                // Credit sits at the window bottom, or at the end of the content while scrolling
                int fy = Math.Max(ClientSize.Height, contentHeight) - S(20) - S(18) - scrollY;
                // Measure on a single line, otherwise the text wraps at the width of its first word
                var cs = TextRenderer.MeasureText(credit.Text, credit.Font, new Size(int.MaxValue, int.MaxValue), TextBlock.OneLine);
                int creditW = cs.Width + S(8);
                credit.SetBounds(left + contentW - creditW, fy, creditW, Math.Max(S(20), cs.Height + S(4)));
            }
            finally
            {
                ResumeLayout(true);
                layoutBusy = false;
                Invalidate(true);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            DoLayoutAll();
        }

        int MaxScroll { get { return Math.Max(0, contentHeight - ClientSize.Height); } }

        // Scrolls the content to y (clamped). Painting is frozen while moving to avoid flicker.
        void ScrollTo(int y)
        {
            y = Math.Max(0, Math.Min(y, MaxScroll));
            if (y == scrollY) return;
            scrollY = y;
            // Freeze painting while controls move, otherwise the window flickers
            const int WM_SETREDRAW = 0x000B;
            Native.SendMessagePtr(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try { DoLayoutAll(); }
            finally
            {
                Native.SendMessagePtr(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                Refresh();
            }
        }

        // Page Up/Down and Ctrl+Home/End scroll the window, except while typing in a text box.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!(ActiveControl is TextBox) && MaxScroll > 0)
            {
                int page = ClientSize.Height * 4 / 5;
                if (keyData == Keys.PageDown) { ScrollTo(scrollY + page); return true; }
                if (keyData == Keys.PageUp) { ScrollTo(scrollY - page); return true; }
                if (keyData == (Keys.Control | Keys.End)) { ScrollTo(MaxScroll); return true; }
                if (keyData == (Keys.Control | Keys.Home)) { ScrollTo(0); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------- Game list

        // Shows the current game list.
        void RefreshGameList()
        {
            gameList.SetItems(settings.Games.ToList());
        }

        // Adds a game by name or exe path. Duplicates are ignored.
        void AddGame(string raw)
        {
            string name = Settings.CleanName(raw);
            if (name.Length == 0) return;
            if (!settings.Games.Any(g => g.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                settings.Games.Add(name);
                settings.Save();
                RefreshGameList();
                Log.Write("Game added: " + name);
            }
            Evaluate();
        }

        // Opens Explorer to pick one or more game executables. The process name equals the exe file name.
        void ChooseExe()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = L.T("games.dialogTitle");
                dlg.Filter = L.T("games.filter");
                dlg.Multiselect = true;
                dlg.CheckFileExists = true;
                dlg.DereferenceLinks = true;
                dlg.InitialDirectory = lastPickDir ?? DefaultGameDir();
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                foreach (var f in dlg.FileNames)
                    if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) AddGame(f);
                if (dlg.FileNames.Length > 0) lastPickDir = Path.GetDirectoryName(dlg.FileNames[0]);
            }
        }

        // Start folder for the file dialog: the Steam games folder if present, otherwise Program Files.
        static string DefaultGameDir()
        {
            try
            {
                string steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common");
                if (Directory.Exists(steam)) return steam;
            }
            catch { }
            return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }

        // Removes a game from the list.
        void RemoveGame(string name)
        {
            int i = settings.Games.FindIndex(g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return;
            settings.Games.RemoveAt(i);
            settings.Save();
            RefreshGameList();
            Log.Write("Game removed: " + name);
            Evaluate();
        }

        // "Ignore" button: excludes the app that was detected by fullscreen or library detection.
        void IgnoreCurrentFullscreenApp()
        {
            string app = policy.ActiveReason == Reason.Fullscreen || policy.ActiveReason == Reason.Library ? policy.ActiveGame : policy.PendingCandidate;
            if (string.IsNullOrEmpty(app)) return;
            if (!settings.FullscreenIgnore.Any(x => x.Equals(app, StringComparison.OrdinalIgnoreCase)))
                settings.FullscreenIgnore.Add(app);
            settings.Save();
            policy.Forget(app);
            Log.Write("Excluded from fullscreen detection: " + app);
            fsHint.Text = IgnoreHintText();
            UpdateMinimumSize();
            DoLayoutAll();
            Evaluate();
        }

        // Context menu listing running apps with a window, to add one of them as a game.
        void ShowPicker(Control anchor)
        {
            if (pickerMenu != null) pickerMenu.Dispose();
            pickerMenu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, Font = Theme.Body };

            int self = Process.GetCurrentProcess().Id;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<KeyValuePair<string, string>>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != self && p.MainWindowHandle != IntPtr.Zero && seen.Add(p.ProcessName))
                        entries.Add(new KeyValuePair<string, string>(p.ProcessName, p.MainWindowTitle));
                }
                catch { }
                finally { p.Dispose(); }
            }

            foreach (var kv in entries.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string name = kv.Key;
                string title = kv.Value ?? "";
                if (title.Length > 42) title = title.Substring(0, 41) + "…";
                var item = new ToolStripMenuItem(title.Length > 0 && title != name ? name + "     " + title : name);
                item.Click += (a, b) => AddGame(name);
                pickerMenu.Items.Add(item);
            }
            if (pickerMenu.Items.Count == 0)
                pickerMenu.Items.Add(new ToolStripMenuItem(L.T("games.pickNone")) { Enabled = false });

            pickerMenu.Show(anchor, new Point(0, anchor.Height + S(4)));
        }

        // ---------- Switching logic

        // Desktop rate for a monitor (stored value or default).
        int IdleFor(MonitorInfo m)
        {
            int v;
            return RateLogic.ResolveIdle(m.Rates, settings.IdleHz.TryGetValue(m.Key, out v) ? (int?)v : null);
        }

        // Battery rate for a monitor (stored value or default).
        int BatteryFor(MonitorInfo m)
        {
            int v;
            return RateLogic.ResolveIdle(m.Rates, settings.BatteryHz.TryGetValue(m.Key, out v) ? (int?)v : null);
        }

        // Rate a monitor should use for the given target.
        int TargetFor(MonitorInfo m, Target t)
        {
            return t == Target.Max ? MaxFor(m) : t == Target.Battery ? BatteryFor(m) : IdleFor(m);
        }

        // Game rate for a monitor (stored value or highest).
        int MaxFor(MonitorInfo m)
        {
            int v;
            return RateLogic.ResolveMax(m.Rates, settings.MaxHz.TryGetValue(m.Key, out v) ? (int?)v : null);
        }

        // Changes between automatic, fixed game mode and fixed desktop mode.
        void SetMode(SwitchMode mode)
        {
            settings.Mode = mode;
            settings.Save();
            modeSel.Selected = (int)mode;
            SyncPolicy();
            Log.Write("Mode changed to " + mode);
            forceApply = true;
            Evaluate();
        }

        // Main loop, called by the timer every few seconds and after every relevant change:
        // detects games, asks SwitchPolicy for the target, switches if RetryState allows it, updates the UI.
        void Evaluate()
        {
            if (exiting) return;
            SyncPolicy();
            string listGame = null, fsApp = null;
            Reason listReason = Reason.GameList;
            bool onBattery = BatteryEnabled && Power.OnBattery;
            if (settings.Mode == SwitchMode.Auto && !onBattery)
            {
                listGame = GameDetector.FindRunningGame(settings.Games);
                if (listGame == null && settings.DetectLibraries)
                {
                    // Pick up newly installed games: rescan the library folders every 10 minutes
                    if ((DateTime.Now - libraryScanTime).TotalMinutes >= 10) RescanLibraries();
                    listGame = GameDetector.FindLibraryGame(libraryRoots, settings.FullscreenIgnore);
                    if (listGame != null) listReason = Reason.Library;
                }
                if (listGame == null && settings.DetectFullscreen) fsApp = GameDetector.FindFullscreenApp(settings.FullscreenIgnore);
            }

            var now = DateTime.Now;
            Target before = policy.Desired;
            Target desired = policy.Update(now, listGame, fsApp, onBattery, listReason);
            if (desired != before)
                Log.Write("Desired state " + before + " -> " + desired +
                    (policy.ActiveGame != null ? " (" + policy.ActiveGame + ".exe, " + policy.ActiveReason + ")" : ""));

            if (forceApply) retry.Reset();
            forceApply = false;
            if (retry.ShouldApply(desired, now)) ApplyTarget(desired);
            UpdateUi();
        }

        // Switches all included monitors to the target, transaction-like:
        // 1. stage the new rate per monitor, 2. apply all at once, 3. read back the active rate,
        // 4. roll back the saved setting of every monitor that did not end up at its target.
        // The result is recorded in RetryState and shown in the UI.
        void ApplyTarget(Target t)
        {
            var before = Display.GetMonitors();
            var staged = new List<KeyValuePair<MonitorInfo, int>>();
            var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in before)
            {
                if (settings.ExcludedMonitors.Contains(m.Key)) continue;
                int target = TargetFor(m, t);
                if (target <= 0) continue;
                // The saved desktop setting already matches. If the active rate differs, a game in exclusive
                // fullscreen has set its own mode: do not interfere, it would minimise the game.
                if (m.RegistryHz == target) continue;

                int rc = Display.StageHz(m.GdiName, target);
                Log.Write(string.Format("Stage {0} ({1}): {2} -> {3} Hz: {4}", m.GdiName, m.FriendlyName, m.RegistryHz, target, Display.DispName(rc)));
                if (rc == 0) staged.Add(new KeyValuePair<MonitorInfo, int>(m, target));
                else errors[m.Key] = new[] { m.FriendlyName, L.F("err.rejected", target, Display.DispName(rc)) };
            }

            int applyRc = 0;
            if (staged.Count > 0)
            {
                applyRc = Display.ApplyStaged();
                Log.Write("Apply: " + Display.DispName(applyRc));
            }

            // Read back the actual state
            var after = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in Display.GetMonitors()) if (!after.ContainsKey(m.Key)) after[m.Key] = m;

            var rollback = new List<KeyValuePair<MonitorInfo, int>>();
            foreach (var kv in staged)
            {
                MonitorInfo now;
                bool ok = after.TryGetValue(kv.Key.Key, out now) && now.ActiveHz == kv.Value;
                if (ok) continue;
                errors[kv.Key.Key] = new[]
                {
                    kv.Key.FriendlyName,
                    applyRc != 0 ? L.F("err.rejected", kv.Value, Display.DispName(applyRc)) : L.F("err.notActive", kv.Value)
                };
                Log.Write("Verify failed for " + kv.Key.GdiName + ": expected " + kv.Value + " Hz, active " + (now != null ? now.ActiveHz : 0) + " Hz");
                rollback.Add(new KeyValuePair<MonitorInfo, int>(kv.Key, kv.Key.RegistryHz));
            }

            // Safe fallback: restore the saved setting of every monitor that did not reach its target
            if (rollback.Count > 0)
            {
                bool any = false;
                foreach (var kv in rollback)
                {
                    if (kv.Value <= 0) continue;
                    int rc = Display.StageHz(kv.Key.GdiName, kv.Value);
                    Log.Write("Rollback stage " + kv.Key.GdiName + " to " + kv.Value + " Hz: " + Display.DispName(rc));
                    if (rc == 0) any = true;
                }
                if (any) Log.Write("Rollback apply: " + Display.DispName(Display.ApplyStaged()));
            }

            bool success = errors.Count == 0;
            var nowTime = DateTime.Now;
            retry.Record(t, success, nowTime);
            monitorErrors = errors;
            if (staged.Count > 0 || !success)
            {
                lastSwitchTime = nowTime;
                lastSwitchTarget = t;
                lastSwitchGame = t == Target.Max ? policy.ActiveGame : null;
            }
            if (!success)
            {
                if (retry.GaveUp) Log.Write("Switching to " + t + " failed, giving up after " + retry.Attempts + " attempts");
                else Log.Write("Switching to " + t + " failed (attempt " + retry.Attempts + "), next attempt at " + retry.NextRetry.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            }
            RefreshMonitors();
        }

        // Names of monitors with a switching error, comma separated.
        string ErrorNames()
        {
            return string.Join(", ", monitorErrors.Values.Select(v => v[0]));
        }

        // Status line below the monitors: last switch, or the current error with retry information.
        string FooterText()
        {
            if (monitorErrors.Count > 0)
            {
                string tail = retry.GaveUp
                    ? L.F("footer.gaveUp", retry.Attempts)
                    : L.F("footer.retryIn", Math.Max(1, (int)Math.Ceiling((retry.NextRetry - DateTime.Now).TotalSeconds)));
                return L.F("footer.failed", ErrorNames()) + " " + tail;
            }
            if (!lastSwitchTime.HasValue) return L.T("footer.none");
            return L.F("footer.switched", lastSwitchTime.Value,
                L.T(lastSwitchTarget == Target.Max ? "footer.gameRate" : lastSwitchTarget == Target.Battery ? "footer.batteryRate" : "footer.desktopRate"),
                lastSwitchGame != null ? L.F("footer.because", lastSwitchGame) : "");
        }

        // Windows display change event (also fired by our own switches); handled on the UI thread.
        void OnDisplayChanged(object sender, EventArgs e)
        {
            if (IsHandleCreated && !exiting) BeginInvoke((Action)RefreshMonitors);
        }

        // Detects real changes (monitor set, resolution, available rates) and rebuilds; otherwise only repaints
        void RefreshMonitors()
        {
            var mons = Display.GetMonitors();
            if (!RateLogic.SameLayout(mons, rows.Select(r => r.Info).ToList()))
            {
                Log.Write("Monitor layout changed, rebuilding and re-applying");
                BuildRows();
                forceApply = true; // applied on the next timer tick at the latest
                if (IsHandleCreated) BeginInvoke((Action)Evaluate);
                return;
            }
            for (int i = 0; i < mons.Count; i++)
            {
                rows[i].Info = mons[i];
                rows[i].Invalidate();
            }
            if (diag != null && !diag.IsDisposed) diag.RefreshContent();
        }

        // Updates header, monitor rows, tray icon, tray menu and status line from the current state.
        void UpdateUi()
        {
            if (header == null || miAuto == null) return;
            var now = DateTime.Now;
            bool game = policy.Desired == Target.Max;
            bool battery = policy.Desired == Target.Battery;

            if (settings.Mode == SwitchMode.Max) { header.Title = L.T("status.fixedGame"); header.Sub = L.T("status.fixed"); }
            else if (settings.Mode == SwitchMode.Idle) { header.Title = L.T("status.fixedDesktop"); header.Sub = L.T("status.fixed"); }
            else if (battery) { header.Title = L.T("status.battery"); header.Sub = L.T("status.batterySub"); }
            else if (game && policy.ActiveGame != null)
            {
                double since = policy.SecondsSinceGame(now);
                header.Title = L.T("status.game");
                header.Sub = since < settings.PollSeconds + 0.5
                    ? L.F(policy.ActiveReason == Reason.Fullscreen ? "status.detectedFs" : policy.ActiveReason == Reason.Library ? "status.detectedLib" : "status.detectedList", policy.ActiveGame)
                    : L.F("status.backIn", Math.Max(0, (int)Math.Ceiling(settings.IdleDelay - since)));
            }
            else if (policy.PendingCandidate != null)
            {
                header.Title = L.T("status.desktop");
                header.Sub = L.F("status.pending", policy.PendingCandidate, Math.Max(1, (int)Math.Ceiling(policy.PendingSecondsLeft(now))));
            }
            else { header.Title = L.T("status.desktop"); header.Sub = L.T("status.waiting"); }

            header.SubColor = Theme.Muted;
            if (monitorErrors.Count > 0)
            {
                header.Sub = L.F("status.partial", ErrorNames());
                header.SubColor = Theme.Error;
            }
            header.Dot = battery ? Theme.Battery : game ? Theme.Game : Theme.Idle;
            header.Invalidate();

            string ignoreApp = settings.Mode != SwitchMode.Auto ? null
                : (policy.ActiveReason == Reason.Fullscreen || policy.ActiveReason == Reason.Library) && policy.ActiveGame != null ? policy.ActiveGame
                : policy.PendingCandidate;
            ignoreBtn.Visible = ignoreApp != null;
            if (ignoreApp != null) ignoreBtn.Text = L.F("btn.ignore", ignoreApp);
            ignoreBtn.Invalidate();

            foreach (var r in rows)
            {
                r.GameActive = game;
                r.BatteryActive = battery;
                r.Excluded = settings.ExcludedMonitors.Contains(r.Info.Key);
                string[] err;
                r.Error = monitorErrors.TryGetValue(r.Info.Key, out err) ? err[1] : null;
                r.Invalidate();
            }

            miAuto.Checked = settings.Mode == SwitchMode.Auto;
            miMax.Checked = settings.Mode == SwitchMode.Max;
            miIdle.Checked = settings.Mode == SwitchMode.Idle;

            tray.Icon = battery ? iconBattery : game ? iconGame : iconIdle;
            if (batteryHint != null) batteryHint.Text = BatteryHintText();
            string tip = "RefreshSwitcher: " + header.Title + (monitorErrors.Count > 0 ? " (!)" : "");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

            if (footer != null)
            {
                string ft = FooterText();
                footer.TextColor = monitorErrors.Count > 0 ? Theme.Error : Theme.Muted;
                if (footer.Text != ft)
                {
                    int h0 = footer.Height;
                    footer.Text = ft;
                    if (footer.MeasureHeight(footer.Width) != h0) DoLayoutAll(); // a longer text needs more room
                }
                footer.Invalidate();
            }
            if (diag != null && !diag.IsDisposed) diag.RefreshContent();
        }

        // ---------- Diagnostics

        // Saved profiles of monitors that are not connected right now.
        List<string> StaleProfiles()
        {
            return settings.StaleProfiles(rows.Select(r => r.Info.Key));
        }

        // Plain-text diagnostics report (always English, like the log) for the diagnostics window.
        string DiagnosticsText()
        {
            var now = DateTime.Now;
            var sb = new StringBuilder();
            var ver = typeof(MainForm).Assembly.GetName().Version;
            sb.AppendLine("RefreshSwitcher " + ver.ToString(3) + "    " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine();
            sb.AppendLine("Mode            " + settings.Mode);
            sb.AppendLine("Desired state   " + policy.Desired);
            sb.AppendLine("Last attempt    " + (retry.Attempts == 0 ? "none" :
                retry.AttemptedTarget + ", attempt " + retry.Attempts + ", " + (retry.Success ? "success" : retry.GaveUp ? "failed, stopped" : "failed, next at " + retry.NextRetry.ToString("HH:mm:ss", CultureInfo.InvariantCulture))));
            sb.AppendLine("Game folders    " + (settings.DetectLibraries ? libraryRoots.Count + (libraryRoots.Count > 0 ? ": " + string.Join(" | ", libraryRoots) : "") : "detection off"));
            sb.AppendLine("Detected        " + (policy.ActiveGame != null ? policy.ActiveGame + ".exe (" + policy.ActiveReason + ")" : "none"));
            sb.AppendLine("Fullscreen      " + (policy.PendingCandidate != null ? policy.PendingCandidate + ".exe, " + Math.Ceiling(policy.PendingSecondsLeft(now)) + " s left" : "none"));
            sb.AppendLine("Power           battery " + (Power.HasBattery ? "present" : "none") + ", " + (Power.OnBattery ? "on battery" : "plugged in") + ", battery mode " + (settings.BatteryMode ? "on" : "off"));
            sb.AppendLine("Timing          check every " + settings.PollSeconds + " s, fullscreen after " + settings.FullscreenDelay + " s, back after " + settings.IdleDelay + " s");
            foreach (var r in rows)
            {
                var m = r.Info;
                string[] err;
                sb.AppendLine();
                sb.AppendLine("[" + m.GdiName + "] " + m.FriendlyName + "  " + m.Width + "x" + m.Height);
                sb.AppendLine("  Active          " + m.ActiveHz + " Hz");
                sb.AppendLine("  Desktop setting " + m.RegistryHz + " Hz");
                sb.AppendLine("  Desktop / game  " + IdleFor(m) + " Hz / " + MaxFor(m) + " Hz" + (BatteryEnabled ? " / battery " + BatteryFor(m) + " Hz" : ""));
                sb.AppendLine("  Switching       " + (settings.ExcludedMonitors.Contains(m.Key) ? "excluded" : "included"));
                sb.AppendLine("  Available       " + string.Join(", ", m.Rates));
                sb.AppendLine("  Last error      " + (monitorErrors.TryGetValue(m.Key, out err) ? err[1] : "none"));
            }
            var stale = StaleProfiles();
            sb.AppendLine();
            sb.AppendLine("Profiles of disconnected monitors: " + stale.Count);
            foreach (var k in stale) sb.AppendLine("  " + k);
            sb.AppendLine();
            sb.AppendLine("Log  " + Log.FilePath);
            return sb.ToString();
        }

        // Opens the diagnostics window, or brings it to the front if it is already open.
        void ShowDiagnostics()
        {
            if (diag == null || diag.IsDisposed)
            {
                diag = new DiagnosticsForm(DiagnosticsText, () => StaleProfiles().Count, () =>
                {
                    var stale = StaleProfiles();
                    settings.RemoveProfiles(stale);
                    settings.Save();
                    Log.Write("Removed " + stale.Count + " profiles of disconnected monitors");
                });
            }
            diag.Show();
            if (diag.WindowState == FormWindowState.Minimized) diag.WindowState = FormWindowState.Normal;
            diag.Activate();
        }

        // ---------- Window and exit

        // Shows and activates the main window from the tray.
        void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        // The close button only hides the window to the tray. Real closing happens on exit or Windows shutdown.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!balloonShown)
                {
                    tray.ShowBalloonTip(3000, "RefreshSwitcher", L.T("tray.balloon"), ToolTipIcon.None);
                    balloonShown = true;
                }
                return;
            }
            if (!exiting)
            {
                exiting = true;
                timer.Stop();
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                SystemEvents.PowerModeChanged -= OnPowerChanged;
                Log.Write("Closing (" + e.CloseReason + ")");
            }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        // Exits the app: restores the game rate on all monitors first, so nobody is left at a low refresh rate.
        void ExitApp()
        {
            Log.Write("Exit requested, restoring game rate");
            exiting = true;
            if (scroller != null) Application.RemoveMessageFilter(scroller);
            timer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.PowerModeChanged -= OnPowerChanged;
            try { retry.Reset(); ApplyTarget(Target.Max); } catch (Exception ex) { Log.Write("Restoring failed: " + ex); }
            if (diag != null && !diag.IsDisposed) diag.Close();
            tray.Visible = false;
            tray.Dispose();
            Application.Exit();
        }
    }

    // ------------------------------------------------------------------ Diagnostics window

    // Diagnostics window: live report of every monitor, the detection state and retry state.
    // Grows with its content and scrolls without a scroll bar if the screen is too small.
    class DiagnosticsForm : Form
    {
        readonly Func<string> source;
        readonly Func<int> staleCount;
        readonly Action removeStale;
        readonly TextBlock text;
        readonly Panel viewport;
        readonly FlatButton copyBtn, logBtn, staleBtn, closeBtn;
        readonly NoBarScroller scroller;
        int scrollY, textHeight;
        readonly System.Windows.Forms.Timer timer;

        public DiagnosticsForm(Func<string> source, Func<int> staleCount, Action removeStale)
        {
            this.source = source;
            this.staleCount = staleCount;
            this.removeStale = removeStale;

            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            text = new TextBlock("", Theme.Mono, Theme.Text);
            viewport = new Panel { BackColor = Theme.Bg };
            viewport.Controls.Add(text);
            copyBtn = new FlatButton("");
            copyBtn.Click += (a, b) => { try { Clipboard.SetText(source()); } catch (Exception ex) { Log.Write("Copy failed: " + ex.Message); } };
            logBtn = new FlatButton("");
            logBtn.Click += (a, b) => Log.Open();
            staleBtn = new FlatButton("");
            staleBtn.Click += (a, b) => { this.removeStale(); RefreshContent(); };
            closeBtn = new FlatButton("");
            closeBtn.Click += (a, b) => Close();
            Controls.AddRange(new Control[] { viewport, copyBtn, logBtn, staleBtn, closeBtn });

            scroller = new NoBarScroller(this, () => MaxScroll, () => scrollY, y => { scrollY = Math.Max(0, Math.Min(y, MaxScroll)); Relayout(); },
                c => false, c => c == this || c == viewport || c == text);
            Application.AddMessageFilter(scroller);

            Retranslate();
            ClientSize = new Size(Theme.S(640), Theme.S(480));

            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (a, b) => RefreshContent();
            timer.Start();
            RefreshContent();
        }

        // Updates the button texts after a language change.
        public void Retranslate()
        {
            Text = L.T("diag.title");
            copyBtn.Text = L.T("diag.copy");
            logBtn.Text = L.T("tray.log");
            closeBtn.Text = L.T("diag.close");
            RefreshContent();
        }

        // Reloads the report text; called every second.
        public void RefreshContent()
        {
            if (IsDisposed) return;
            text.Text = source();
            int n = staleCount();
            staleBtn.Text = L.F("diag.stale", n);
            staleBtn.Visible = n > 0;
            Relayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DarkWindow.Apply(this);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        int MaxScroll { get { return Math.Max(0, textHeight - viewport.Height); } }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Application.RemoveMessageFilter(scroller);
            timer.Stop();
            timer.Dispose();
            base.OnFormClosed(e);
        }

        // Sizes the window to its content and positions the report and the buttons.
        void Relayout()
        {
            if (text == null || closeBtn == null || viewport == null) return;
            int pad = Theme.S(20), btnH = Theme.S(32), gap = Theme.S(10);
            int w = ClientSize.Width - 2 * pad;
            int textH = text.MeasureHeight(w);
            int needed = pad + textH + Theme.S(16) + btnH + pad;
            var min = SizeFromClientSize(new Size(Theme.S(520), needed));
            var wa = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(min.Width, Math.Min(min.Height, wa.Height));
            if (ClientSize.Height < needed && Height < wa.Height) return; // MinimumSize enlarges the window; OnResize calls Relayout again

            // If the text still does not fit (high scaling, small screen), it scrolls inside the viewport
            int by = ClientSize.Height - pad - btnH;
            int viewH = Math.Max(Theme.S(40), by - Theme.S(16) - pad);
            textHeight = textH;
            viewport.SetBounds(pad, pad, w, viewH);
            scrollY = Math.Max(0, Math.Min(scrollY, MaxScroll));
            text.SetBounds(0, -scrollY, w, textH);
            int bw = Theme.S(150);
            copyBtn.SetBounds(pad, by, bw, btnH);
            logBtn.SetBounds(pad + bw + gap, by, bw, btnH);
            closeBtn.SetBounds(ClientSize.Width - pad - Theme.S(120), by, Theme.S(120), btnH);
            int sw = Math.Max(Theme.S(120), closeBtn.Left - gap - (logBtn.Right + gap));
            staleBtn.SetBounds(logBtn.Right + gap, by, Math.Min(sw, Theme.S(260)), btnH);
        }
    }

    // ------------------------------------------------------------------ Logging

    // Log file in %APPDATA%\RefreshSwitcher. Always English so bug reports are readable for everyone.
    // Appends across sessions and rotates at 1 MB to RefreshSwitcher.old.log. Thread-safe.
    static class Log
    {
        static readonly object Sync = new object();
        const long MaxBytes = 1024 * 1024;
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RefreshSwitcher");
        public static string FilePath { get { return Path.Combine(Dir, "RefreshSwitcher.log"); } }
        static string OldPath { get { return Path.Combine(Dir, "RefreshSwitcher.old.log"); } }

        // Appends one timestamped line. Never throws.
        public static void Write(string msg)
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        if (File.Exists(OldPath)) File.Delete(OldPath);
                        File.Move(FilePath, OldPath);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }

        // Opens the log in Notepad.
        public static void Open()
        {
            try
            {
                Write("Opening log");
                // Fixed path to Notepad in System32, no PATH lookup that another program could hijack
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + FilePath + "\"") { UseShellExecute = false };
                Process.Start(psi);
            }
            catch (Exception ex) { Write("Opening log failed: " + ex.Message); }
        }

        // Logs an unexpected exception and shows it to the user in the current UI language.
        public static void Crash(string where, Exception ex)
        {
            Write("ERROR in " + where + ": " + ex);
            try
            {
                MessageBox.Show(L.F("msg.crash", where, ex.GetType().Name, ex.Message, FilePath), "RefreshSwitcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }

    // Entry point: command line handling, single-instance check, settings, theme and main window.
    static class Program
    {
        public const string ExitEventName = "RefreshSwitcher.Exit";

        // Arguments:
        // --exit  asks a running instance to quit cleanly (used by the installer and uninstaller).
        // --tray  starts in the notification area only.
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase)))
            {
                try { using (var ev = EventWaitHandle.OpenExisting(ExitEventName)) ev.Set(); } catch { }
                return;
            }

            // English .NET error texts, so the log is readable regardless of the Windows language
            var en = CultureInfo.GetCultureInfo("en-US");
            Thread.CurrentThread.CurrentUICulture = en;
            CultureInfo.DefaultThreadCurrentUICulture = en;

            // Remove the log file name used up to version 1.2
            try { File.Delete(Path.Combine(Log.Dir, "start.log")); } catch { }
            Log.Write("===== Start " + typeof(Program).Assembly.GetName().Version + ", .NET " + Environment.Version +
                ", Windows " + Environment.OSVersion.Version + ", 64-bit: " + Environment.Is64BitProcess + " =====");

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (a, e) => Log.Crash("UI thread", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (a, e) => Log.Crash("process", e.ExceptionObject as Exception ?? new Exception("unknown"));

            try
            {
                try { Native.SetProcessDPIAware(); } catch { }

                Log.Write("Loading settings");
                var settings = Settings.Load();
                L.Current = settings.Language;
                Theme.Apply(Palette.Get(settings.Theme));
                Log.Write("Language: " + settings.Language);

                bool created;
                using (var mutex = new Mutex(true, "RefreshSwitcher.SingleInstance", out created))
                {
                    if (!created)
                    {
                        Log.Write("Second instance, exiting");
                        MessageBox.Show(L.T("msg.already"), "RefreshSwitcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Log.Write("Loading theme");
                    Theme.Init();
                    bool hidden = settings.StartInTray || args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
                    Log.Write("Creating window (hidden: " + hidden + ")");
                    var form = new MainForm(settings, hidden);
                    Log.Write("Window created, starting message loop");
                    Application.Run(form);
                    Log.Write("Exited");
                    GC.KeepAlive(mutex);
                }
            }
            catch (Exception ex)
            {
                Log.Crash("startup", ex);
            }
        }
    }
}
