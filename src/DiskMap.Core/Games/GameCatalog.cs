using System.Text.Json;
using Microsoft.Win32;

namespace DiskMap.Core.Games;

public sealed record GameInfo(string Title, string Launcher, string InstallPath, string? UninstallUri, string? UninstallCommand, DateTime? LastPlayed);

/// <summary>Installed games from Steam, Epic, GOG and Battle.net, keyed by install folder.</summary>
public sealed class GameCatalog
{
    private readonly Dictionary<string, GameInfo> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byPath.Count;

    public bool TryGet(string folder, out GameInfo game) => _byPath.TryGetValue(folder.TrimEnd('\\'), out game!);

    public static GameCatalog Load()
    {
        var catalog = new GameCatalog();
        Try(() => catalog.LoadSteam());
        Try(() => catalog.LoadEpic());
        Try(() => catalog.LoadGog());
        Try(() => catalog.LoadUninstallEntries());
        return catalog;
    }

    private static void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or System.Security.SecurityException) { }
    }

    private void Add(GameInfo g)
    {
        var key = Path.GetFullPath(g.InstallPath).TrimEnd('\\');
        _byPath.TryAdd(key, g with { InstallPath = key });
    }

    public static IEnumerable<string> SteamLibraries()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrEmpty(steam)) yield break;
        steam = steam.Replace('/', '\\');
        var vdfPath = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };
        yield return steam;
        if (!File.Exists(vdfPath)) yield break;
        var root = Vdf.Parse(File.ReadAllText(vdfPath));
        if (root.Get("libraryfolders") is not Vdf.Node folders) yield break;
        foreach (var (_, value) in folders.Children)
        {
            if (value is Vdf.Node lib && lib.Get("path") is string path && seen.Add(path)) yield return path;
        }
    }

    private void LoadSteam()
    {
        foreach (var library in SteamLibraries())
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var state = Vdf.Parse(File.ReadAllText(manifest)).Get("AppState") as Vdf.Node;
                if (state?.Get("appid") is not string appId || state.Get("installdir") is not string dir) continue;
                var name = state.Get("name") as string ?? dir;
                DateTime? played = null;
                if (state.Get("LastPlayed") is string lp && long.TryParse(lp, out var unix) && unix > 0)
                {
                    played = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                }
                Add(new GameInfo(name, "Steam", Path.Combine(apps, "common", dir), $"steam://uninstall/{appId}", null, played));
            }
        }
    }

    private void LoadEpic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) return;
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(item));
            var r = doc.RootElement;
            if (!r.TryGetProperty("InstallLocation", out var loc) || loc.GetString() is not string path) continue;
            var title = r.TryGetProperty("DisplayName", out var dn) ? dn.GetString() ?? Path.GetFileName(path) : Path.GetFileName(path);
            Add(new GameInfo(title, "Epic Games", path, "com.epicgames.launcher://store/library", null, null));
        }
    }

    private void LoadGog()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (key == null) return;
        foreach (var id in key.GetSubKeyNames())
        {
            using var g = key.OpenSubKey(id);
            if (g?.GetValue("path") is string path && g.GetValue("gameName") is string name)
            {
                Add(new GameInfo(name, "GOG Galaxy", path, null, g.GetValue("uninstallCommand") as string, null));
            }
        }
    }

    /// <summary>Battle.net, Riot and other launchers register their games as normal uninstall entries.</summary>
    private void LoadUninstallEntries()
    {
        string[] publishers = ["Blizzard Entertainment", "Riot Games", "Ubisoft", "Electronic Arts", "Rockstar Games"];
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var e = key.OpenSubKey(sub);
                    if (e?.GetValue("Publisher") is not string publisher || !publishers.Any(p => publisher.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
                    if (e.GetValue("InstallLocation") is not string loc || string.IsNullOrWhiteSpace(loc)) continue;
                    if (e.GetValue("DisplayName") is not string name || name.Contains("Launcher", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;
                    var launcher = publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) ? "Battle.net" : publisher;
                    Add(new GameInfo(name, launcher, loc.Trim('"'), null, e.GetValue("UninstallString") as string, null));
                }
            }
        }
    }
}

/// <summary>Minimal reader for Valve's KeyValues text format (.vdf / .acf).</summary>
internal static class Vdf
{
    public sealed class Node
    {
        public List<(string Key, object Value)> Children { get; } = new();

        public object? Get(string key)
        {
            foreach (var (k, v) in Children)
            {
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return v;
            }
            return null;
        }
    }

    public static Node Parse(string text)
    {
        int i = 0;
        return ParseNode(text, ref i);
    }

    private static Node ParseNode(string s, ref int i)
    {
        var node = new Node();
        while (true)
        {
            var key = Token(s, ref i);
            if (key == null || key == "}") return node;
            var value = Token(s, ref i);
            if (value == null) return node;
            node.Children.Add(value == "{" ? (key, ParseNode(s, ref i)) : (key, value));
        }
    }

    private static string? Token(string s, ref int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c is '{' or '}') { i++; return c.ToString(); }
            if (c == '"')
            {
                var sb = new System.Text.StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { i++; sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] }); }
                    else sb.Append(s[i]);
                    i++;
                }
                i++;
                return sb.ToString();
            }
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '"')) i++;
            return s[start..i];
        }
        return null;
    }
}
