// RefreshSwitcher, Copyright (C) 2026 SmokingTux
// SPDX-License-Identifier: GPL-3.0-or-later
// This program is free software under the GNU General Public License v3.0 or later. See LICENSE.
//
// Tests for the parts of RefreshSwitcher that do not call Windows: rate selection, the switching decision,
// retries, game-library parsing, settings parsing, translations and theme contrast.
// No test framework on purpose, so it builds with the compiler that ships with Windows.
//
// Build and run on Windows:
//   csc /main:RefreshSwitcher.Tests /out:Tests.exe RefreshSwitcher.cs Tests.cs && Tests.exe
// Build and run with Mono:
//   mcs -langversion:5 -sdk:4.5 -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Core.dll
//       -main:RefreshSwitcher.Tests -out:Tests.exe RefreshSwitcher.cs Tests.cs && mono Tests.exe
// Exit code 0 means all tests passed.
using System;
using System.Collections.Generic;
using System.Linq;

namespace RefreshSwitcher
{
    // Minimal self-contained test runner. Prints one line per check and returns the number of failures as exit code.
    static class Tests
    {
        static int passed, failed;   // counters for the summary line

        // Records one check result and prints it.
        static void Check(string name, bool ok)
        {
            if (ok) passed++; else failed++;
            Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + name);
        }

        // Builds a fake monitor for layout comparisons.
        static MonitorInfo Mon(string key, int w, int h, params int[] rates)
        {
            return new MonitorInfo { Key = key, GdiName = key, FriendlyName = key, Width = w, Height = h, Rates = rates };
        }

        // Runs all test groups. A fixed start time (t0) keeps the time-based checks deterministic.
        static int Main()
        {
            var t0 = new DateTime(2026, 10, 21, 12, 0, 0);

            Console.WriteLine("RateLogic");
            int[] aw = { 24, 25, 30, 48, 50, 59, 60, 100, 119, 120, 144, 165, 240 };
            Check("Default desktop rate is 60 Hz", RateLogic.DefaultIdle(aw) == 60);
            Check("Without 60 Hz: next higher rate", RateLogic.DefaultIdle(new[] { 48, 75, 144 }) == 75);
            Check("Only low rates: highest one", RateLogic.DefaultIdle(new[] { 24, 30, 50 }) == 50);
            Check("Stored rate is rounded to an available rate", RateLogic.ResolveMax(new[] { 60, 120, 144 }, 165) == 144);
            Check("Without stored rate: maximum", RateLogic.ResolveMax(aw, null) == 240);
            Check("Tie rounds up", RateLogic.Nearest(new[] { 100, 120 }, 110) == 120);
            Check("Empty rate list returns 0", RateLogic.ResolveIdle(new int[0], null) == 0);

            var a = new List<MonitorInfo> { Mon("A", 3840, 2160, 60, 120, 240) };
            Check("Same layout recognised", RateLogic.SameLayout(a, new List<MonitorInfo> { Mon("A", 3840, 2160, 60, 120, 240) }));
            Check("Height change detected", !RateLogic.SameLayout(a, new List<MonitorInfo> { Mon("A", 3840, 1600, 60, 120, 240) }));
            Check("Changed rates detected", !RateLogic.SameLayout(a, new List<MonitorInfo> { Mon("A", 3840, 2160, 60, 120) }));
            Check("Monitor change detected", !RateLogic.SameLayout(a, new List<MonitorInfo> { Mon("B", 3840, 2160, 60, 120, 240) }));

            Console.WriteLine("SwitchPolicy");
            var p = new SwitchPolicy { IdleDelaySec = 6, StabilitySec = 3 };
            Check("Start without game: desktop", p.Update(t0, null, null) == Target.Idle);
            Check("Listed game switches immediately", p.Update(t0.AddSeconds(2), "RocketLeague", null) == Target.Max && p.ActiveReason == Reason.GameList);
            Check("Right after the game ends: still game rate", p.Update(t0.AddSeconds(4), null, null) == Target.Max);
            Check("After the delay: back to desktop", p.Update(t0.AddSeconds(8.1), null, null) == Target.Idle && p.ActiveGame == null);

            p = new SwitchPolicy { IdleDelaySec = 6, StabilitySec = 3 };
            p.Update(t0, null, null);
            Check("Fullscreen: candidate first", p.Update(t0.AddSeconds(1), null, "video") == Target.Idle && p.PendingCandidate == "video");
            Check("Fullscreen: game after the stability delay", p.Update(t0.AddSeconds(4), null, "video") == Target.Max && p.ActiveReason == Reason.Fullscreen);
            p = new SwitchPolicy { IdleDelaySec = 6, StabilitySec = 3 };
            p.Update(t0, null, null);
            p.Update(t0.AddSeconds(1), null, "a");
            Check("Short foreground window triggers nothing", p.Update(t0.AddSeconds(2), null, null) == Target.Idle && p.PendingCandidate == null);
            p.Update(t0.AddSeconds(3), null, "a");
            Check("New candidate restarts the timer", p.Update(t0.AddSeconds(5), null, "b") == Target.Idle);
            Check("Game list wins over the fullscreen delay", p.Update(t0.AddSeconds(5.5), "game", "b") == Target.Max && p.ActiveReason == Reason.GameList);

            p = new SwitchPolicy { IdleDelaySec = 6, StabilitySec = 0 };
            p.Update(t0, null, "chrome");
            p.Forget("chrome");
            Check("Ignore resets the game time", p.Update(t0.AddSeconds(1), null, null) == Target.Idle);

            p = new SwitchPolicy { Mode = SwitchMode.Max };
            Check("Fixed game mode", p.Update(t0, null, null) == Target.Max && p.ActiveGame == null);
            p.Mode = SwitchMode.Idle;
            Check("Fixed desktop mode ignores games", p.Update(t0, "RocketLeague", null) == Target.Idle);

            Console.WriteLine("Battery mode");
            p = new SwitchPolicy { IdleDelaySec = 6, StabilitySec = 3 };
            Check("On battery: battery target", p.Update(t0, null, null, true) == Target.Battery);
            Check("Battery wins over a game", p.Update(t0.AddSeconds(2), "RocketLeague", null, true) == Target.Battery && p.ActiveGame == null);
            Check("Plugged in with game: game at once", p.Update(t0.AddSeconds(4), "RocketLeague", null, false) == Target.Max);
            Check("Unplugged again: battery at once", p.Update(t0.AddSeconds(5), "RocketLeague", null, true) == Target.Battery);
            Check("Plugged in without game: desktop at once", p.Update(t0.AddSeconds(6), null, null, false) == Target.Idle);
            p.Mode = SwitchMode.Max;
            Check("Fixed game mode wins over battery", p.Update(t0.AddSeconds(7), null, null, true) == Target.Max);

            Console.WriteLine("Game libraries");
            string vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}";
            var libs = LibraryLogic.ParseSteamLibraryFolders(vdf);
            Check("Steam libraries parsed", libs.SequenceEqual(new[] { "C:\\Program Files (x86)\\Steam", "D:\\SteamLibrary" }));
            Check("Epic install location parsed", LibraryLogic.ParseEpicInstallLocation("{ \"DisplayName\": \"Fortnite\", \"InstallLocation\": \"E:\\\\Epic Games\\\\Fortnite\" }") == "E:\\Epic Games\\Fortnite");
            var roots = new[] { LibraryLogic.NormalizeRoot("D:\\SteamLibrary\\steamapps\\common") };
            Check("Game inside a library folder detected", LibraryLogic.IsUnder("d:\\steamlibrary\\steamapps\\common\\Rocket League\\RocketLeague.exe", roots));
            Check("Similar folder name does not match", !LibraryLogic.IsUnder("D:\\SteamLibrary\\steamapps\\common2\\x.exe", roots));
            Check("Crash reporter filtered out", LibraryLogic.IsHelper("UnityCrashHandler64", "D:\\Lib\\Game\\UnityCrashHandler64.exe"));
            Check("Anti-cheat filtered out", LibraryLogic.IsHelper("EasyAntiCheat_EOS", "D:\\Lib\\Game\\EasyAntiCheat\\x.exe"));
            Check("Wallpaper Engine filtered out", LibraryLogic.IsHelper("wallpaper64", "D:\\SteamLibrary\\steamapps\\common\\wallpaper_engine\\wallpaper64.exe"));
            Check("Real game is kept", !LibraryLogic.IsHelper("RocketLeague", "D:\\SteamLibrary\\steamapps\\common\\rocketleague\\Binaries\\Win64\\RocketLeague.exe"));
            var lp = new SwitchPolicy { IdleDelaySec = 6, StabilitySec = 3 };
            Check("Library game switches immediately", lp.Update(t0, "eldenring", null, false, Reason.Library) == Target.Max && lp.ActiveReason == Reason.Library);
            Check("Library detection is on by default", new Settings().DetectLibraries);

            Console.WriteLine("RetryState");
            var r = new RetryState();
            Check("First attempt needed", r.ShouldApply(Target.Max, t0));
            Check("No attempt without a target", !r.ShouldApply(Target.None, t0));
            r.Record(Target.Max, false, t0);
            Check("Failure: no immediate retry", !r.ShouldApply(Target.Max, t0.AddSeconds(1)));
            Check("Failure: retry after 5 s", r.ShouldApply(Target.Max, t0.AddSeconds(5)));
            r.Record(Target.Max, false, t0.AddSeconds(5));
            r.Record(Target.Max, false, t0.AddSeconds(20));
            r.Record(Target.Max, false, t0.AddSeconds(50));
            Check("Stopped after 4 failed attempts", r.GaveUp && !r.ShouldApply(Target.Max, t0.AddHours(1)));
            Check("New target tries again", r.ShouldApply(Target.Idle, t0.AddHours(1)));
            r.Record(Target.Idle, true, t0.AddHours(1));
            Check("Success: no further attempt", !r.ShouldApply(Target.Idle, t0.AddHours(2)) && r.Attempts == 1);
            r.Reset();
            Check("Reset forces an attempt", r.ShouldApply(Target.Idle, t0.AddHours(2)));

            Console.WriteLine("Settings");
            var s = Settings.ParseLines(new[]
            {
                "mode=Max", "games=RocketLeague.exe;C:\\Games\\bf6.exe;a<b;rocketleague", "delay=9999", "fsdelay=5", "poll=0",
                "lang=De", "battery=1", "battery.\\\\?\\DISPLAY#X=48", "battery.\\\\?\\DISPLAY#Z=2000", "idle.\\\\?\\DISPLAY#X=60", "max.\\\\?\\DISPLAY#X=abc", "exclude.\\\\?\\DISPLAY#Y=1", "mode=7", "unknown=1", "kaputt"
            });
            Check("Mode parsed, invalid mode ignored", s.Mode == SwitchMode.Max);
            Check("Game names cleaned and deduplicated", s.Games.SequenceEqual(new[] { "RocketLeague", "bf6" }));
            Check("Out-of-range delay ignored", s.IdleDelay == 6);
            Check("Fullscreen delay parsed", s.FullscreenDelay == 5);
            Check("Invalid poll interval ignored", s.PollSeconds == 2);
            Check("Language parsed", s.Language == Lang.De);
            Check("Valid rate parsed, invalid ignored", s.IdleHz.Count == 1 && s.MaxHz.Count == 0);
            Check("Battery mode and valid battery rate parsed", s.BatteryMode && s.BatteryHz.Count == 1 && s.BatteryHz["\\\\?\\DISPLAY#X"] == 48);
            Check("Monitor exclusion parsed", s.ExcludedMonitors.Contains("\\\\?\\DISPLAY#Y"));
            Check("Stale profiles found", s.StaleProfiles(new[] { "\\\\?\\DISPLAY#X" }).SequenceEqual(new[] { "\\\\?\\DISPLAY#Y" }));
            s.RemoveProfiles(s.StaleProfiles(new string[0]));
            Check("Profiles removed", s.IdleHz.Count == 0 && s.BatteryHz.Count == 0 && s.ExcludedMonitors.Count == 0);

            Console.WriteLine("Languages");
            var langs = (Lang[])Enum.GetValues(typeof(Lang));
            Check("5 languages", langs.Length == 5);
            Check("Every text exists in all languages", L.Keys.All(k => L.Count(k) == langs.Length));
            bool formatOk = true;
            foreach (var lang in langs)
            {
                L.Current = lang;
                foreach (var k in L.Keys)
                {
                    try { string.Format(L.T(k), t0, "x", "y", "z"); }
                    catch { formatOk = false; Console.WriteLine("    Format error " + lang + " " + k); }
                }
            }
            L.Current = Lang.En;
            Check("All texts can be formatted", formatOk);
            Check("Ukrainian parsed from settings", Settings.ParseLines(new[] { "lang=Uk" }).Language == Lang.Uk);
            Check("Unknown language ignored", Settings.ParseLines(new[] { "lang=9" }).Language == Lang.En);

            Console.WriteLine("Themes");
            foreach (ThemeId id in Enum.GetValues(typeof(ThemeId)))
            {
                var pal = Palette.Get(id);
                Check(id + ": text clearly readable (>= 7:1)", Palette.Contrast(pal.Text, pal.Bg) >= 7 && Palette.Contrast(pal.Text, pal.Surface) >= 7);
                Check(id + ": secondary text readable (>= 4.5:1)", Palette.Contrast(pal.Muted, pal.Bg) >= 4.5);
                Check(id + ": error text readable (>= 4.5:1)", Palette.Contrast(pal.Error, pal.Bg) >= 4.5);
                Check(id + ": state colours distinguishable (>= 3:1)",
                    Palette.Contrast(pal.Idle, pal.Bg) >= 3 && Palette.Contrast(pal.Game, pal.Bg) >= 3 && Palette.Contrast(pal.Battery, pal.Bg) >= 3);
                Check(id + ": surfaces distinguishable", pal.Surface != pal.Bg && pal.SurfaceHi != pal.Surface);
            }
            Check("Theme parsed from settings", Settings.ParseLines(new[] { "theme=Oled" }).Theme == ThemeId.Oled);
            Check("Unknown theme ignored", Settings.ParseLines(new[] { "theme=Rainbow" }).Theme == ThemeId.Oled);
            Check("Old theme names migrated", Settings.ParseLines(new[] { "theme=Midnight" }).Theme == ThemeId.Ocean
                && Settings.ParseLines(new[] { "theme=Plum" }).Theme == ThemeId.Graphite && Settings.ParseLines(new[] { "theme=Neon" }).Theme == ThemeId.Graphite
                && Settings.ParseLines(new[] { "theme=Graphite" }).Theme == ThemeId.Graphite);

            Console.WriteLine();
            Console.WriteLine(passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
    }
}
