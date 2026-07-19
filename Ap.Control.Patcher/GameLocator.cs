using Microsoft.Win32;

namespace Ap.Control.Patcher
{
    /// <summary>
    /// Finds Control's install directory. The original Python patchers hardcoded one absolute path,
    /// which works on exactly one machine; anything distributed to players has to look for itself.
    ///
    /// Order: an explicit --game path, then Steam (registry, then every library in libraryfolders.vdf),
    /// then the usual fixed locations for Steam / Epic / GOG installs.
    /// </summary>
    internal static class GameLocator
    {
        private const string GameFolder = "Control";

        /// <summary>A directory is the game if it holds the package files we patch.</summary>
        internal static bool IsGameDir(string dir) => Directory.Exists(Path.Combine(dir, "data_packfiles"));

        /// <summary>Resolve the install dir, or throw with guidance on what to pass instead.</summary>
        internal static string Resolve(string? explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                string dir = Path.GetFullPath(explicitPath);
                if (!IsGameDir(dir))
                    throw new PatchException(
                        $"--game path has no data_packfiles folder: {dir}\n" +
                        "Point it at the folder containing Control_DX12.exe.");
                return dir;
            }

            foreach (string candidate in Candidates())
                if (IsGameDir(candidate))
                    return candidate;

            throw new PatchException(
                "could not find Control automatically.\n" +
                "Pass the install folder explicitly, e.g.:\n" +
                "  Ap.Control.Patcher status --game \"D:\\SteamLibrary\\steamapps\\common\\Control\"");
        }

        private static IEnumerable<string> Candidates()
        {
            foreach (string lib in SteamLibraries())
                yield return Path.Combine(lib, "steamapps", "common", GameFolder);

            // Non-Steam and unusual-but-common layouts.
            foreach (string root in FixedRoots())
            {
                yield return Path.Combine(root, "Steam", "steamapps", "common", GameFolder);
                yield return Path.Combine(root, "SteamLibrary", "steamapps", "common", GameFolder);
                yield return Path.Combine(root, "Epic Games", GameFolder);
                yield return Path.Combine(root, "GOG Galaxy", "Games", GameFolder);
                yield return Path.Combine(root, GameFolder);
            }
        }

        private static IEnumerable<string> FixedRoots()
        {
            foreach (var v in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            })
                if (!string.IsNullOrEmpty(v)) yield return v;

            // Games are routinely parked on a second drive, so sweep fixed drive roots too.
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed && d.IsReady)
                    yield return d.RootDirectory.FullName;
        }

        /// <summary>Steam's own install plus every configured library folder.</summary>
        private static IEnumerable<string> SteamLibraries()
        {
            string? steam = SteamPath();
            if (steam is null) yield break;

            yield return steam;

            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) yield break;

            // The VDF is a small nested key/value text format. Rather than take a parser dependency for
            // one file, pull the "path" values out directly — they are the only thing needed here.
            foreach (string line in File.ReadLines(vdf))
            {
                string t = line.Trim();
                if (!t.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;

                int open = t.IndexOf('"', 6);
                int close = open < 0 ? -1 : t.IndexOf('"', open + 1);
                if (open >= 0 && close > open)
                    yield return t[(open + 1)..close].Replace(@"\\", @"\");
            }
        }

        private static string? SteamPath()
        {
            // Registry is authoritative when present; a missing or unreadable key just means we fall
            // through to the fixed-path sweep, so failure here is not worth surfacing.
            try
            {
                foreach (var (hive, key, name) in new[]
                {
                    (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                    (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                    (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                })
                {
                    using RegistryKey? k = hive.OpenSubKey(key);
                    if (k?.GetValue(name) is string p && Directory.Exists(p))
                        return p.Replace('/', '\\');
                }
            }
            catch (Exception e) when (e is IOException or System.Security.SecurityException
                                       or UnauthorizedAccessException)
            {
                // fall through
            }
            return null;
        }
    }
}
