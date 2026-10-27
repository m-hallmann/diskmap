using DiskMap.Core.Actions;
using DiskMap.Core.Games;
using DiskMap.Core.History;
using DiskMap.Core.Model;
using DiskMap.Core.Scanning;

namespace DiskMap.Core.Demo;

/// <summary>
/// A made-up 1 TB drive that is almost full, used for screenshots and for trying diskmap without
/// scanning anything (start with --demo). All names are fictional or generic, and nothing here reads
/// from or writes to the real disk.
/// </summary>
public static class DemoData
{
    private const double GB = 1L << 30;
    public const long TotalBytes = (long)(931.5 * GB);
    public const long FreeBytes = (long)(18.4 * GB);
    public const string SteamLibrary = @"C:\Program Files (x86)\Steam";

    public static ScanResult Create(out Snapshot baseline, out List<FreeSpaceSample> freeSpace)
    {
        var now = DateTime.Now;
        DiskNode Dir(string name, params DiskNode[] kids)
        {
            var d = new DiskNode(name, NodeFlags.Directory) { Modified = now.AddDays(-3) };
            foreach (var k in kids) d.AddChild(k);
            return d;
        }
        DiskNode F(string name, double gb, int daysAgo = 40, long files = 1) => new(name, NodeFlags.None)
        {
            Size = (long)(gb * GB), Logical = (long)(gb * GB), FileCount = files, Modified = now.AddDays(-daysAgo),
        };

        var root = Dir("C:\\",
            Dir("Users",
                Dir("alex",
                    Dir("AI",
                        Dir("llm-models",
                            Dir("openweight-120b",
                                F("openweight-120b-mxfp4-00001-of-00002.gguf", 47.5, 0),
                                F("openweight-120b-mxfp4-00002-of-00002.gguf", 15.9, 0)),
                            F("llama-70b-instruct-q4_k_m.gguf", 42.5, 4),
                            F("coder-30b-instruct-q8_0.gguf", 32.5, 27),
                            F("tiny-4b-chat-q6_k.gguf", 3.6, 76)),
                        Dir("image-models",
                            Dir("checkpoints",
                                F("flux-dev-fp8.safetensors", 17.2), F("sd3.5-large.safetensors", 15.1),
                                F("sdxl-base-1.0.safetensors", 6.9), F("photoreal-xl-v5.safetensors", 6.6),
                                F("illustration-xl-v9.safetensors", 6.5)),
                            Dir("diffusion_models",
                                F("flux-dev.safetensors", 23.8), F("text2video-1.3b.safetensors", 2.8), F("video-gen-2b.safetensors", 2.0)),
                            Dir("loras", F("212 LoRA files.safetensors", 9.1, files: 212)),
                            Dir("text_encoders", F("t5xxl_fp16.safetensors", 3.6), F("clip_l.safetensors", 0.7)),
                            Dir("vae", F("ae.safetensors", 2.1)))),
                    Dir("Documents",
                        Dir("3D projects", F("city-scene.blend", 4.2), F("character-rig.blend", 3.1), F("archive.blend", 2.4)),
                        F("Other documents.pdf", 12.4, files: 3_120)),
                    Dir("AppData",
                        Dir("Local",
                            Dir("D3DSCache", F("shader cache.cache", 11.9, files: 18_400)),
                            Dir("Temp", F("temporary files.tmp", 9.8, 0, files: 6_210)),
                            Dir("PhotoStudio", F("plugins.dll", 6.4, files: 9_800)),
                            Dir("Microsoft", F("Edge and Teams.dll", 6.1, files: 21_000)),
                            Dir("pip", Dir("cache", F("wheels.cache", 5.4, files: 3_300))),
                            Dir("Packages", F("Store apps.dll", 4.3, files: 7_100)),
                            Dir("NVIDIA", Dir("DXCache", F("shaders.cache", 4.1, files: 1_900))),
                            Dir("Google", Dir("Chrome", F("User Data.dll", 3.8, files: 12_000))),
                            Dir("npm-cache", F("_cacache.cache", 3.2, files: 41_000)),
                            Dir("ChatApp", F("app.dll", 2.7, files: 3_300)),
                            Dir("NoteTaker", F("app.dll", 0.9, files: 1_200)),
                            F("Other app data.dll", 5.2, files: 30_000))),
                    Dir("Downloads",
                        F("dataset-images.zip", 7.4, 43), F("Win11_25H2_English_x64.iso", 6.1, 25),
                        F("trailer_final_v3.mov", 5.8, 19), F("vacation-2026.mp4", 5.5, 58),
                        F("3d-assets-pack.zip", 4.8, 117), F("game_mods_backup.7z", 3.9, 131),
                        F("screen-capture-2026-10-12.mp4", 3.2, 15), F("ai-tools-portable.7z", 2.6, 175),
                        F("IDE-Setup-x64.exe", 2.1, 1), F("141 more installers.exe", 10.4, 200, 141),
                        F("403 other files.txt", 4.1, 90, 403)),
                    Dir("Videos", Dir("Captures",
                        F("2026-10-25 21-14-03.mkv", 6.8, 2), F("2026-10-24 20-02-41.mkv", 5.9, 3),
                        F("2026-10-22 19-30-12.mkv", 4.1, 5), F("2026-10-21 22-45-09.mkv", 3.7, 6),
                        F("2026-10-19 18-11-55.mkv", 3.3, 8), F("2026-10-12 21-03-27.mkv", 2.9, 15),
                        F("32 older recordings.mkv", 15.0, 60, 32))),
                    Dir("Pictures",
                        Dir("2025", F("1,834 photos.heic", 7.9, 200, 1_834)),
                        Dir("2024", F("1,210 photos.jpg", 5.2, 500, 1_210)),
                        Dir("2026", F("1,062 photos.heic", 4.6, 10, 1_062)),
                        Dir("Screenshots", F("screenshots.png", 0.7, 3, 420))),
                    Dir("Desktop", F("stuff.zip", 6.2, 12)),
                    F("Other folders.dat", 3.4, files: 5_000))),
            Dir("Program Files (x86)",
                Dir("Steam", Dir("steamapps",
                    Dir("common",
                        Dir("Ironvale Chronicles",
                            Dir("Data", F("Levels.pak", 48.6, 3), F("Textures.pak", 25.2, 3), F("Sounds.pak", 13.9, 3), F("Animations.pak", 8.6, 3)),
                            Dir("Localization", F("English.pak", 11.4, 3)),
                            Dir("Engine", F("Engine.pak", 3.4, 3)),
                            Dir("Bin", F("Ironvale.exe", 3.1, 3))),
                        Dir("Hearthfield Kingdoms", F("Content.pak", 22.9, 30), F("Engine.pak", 0.9, 30)),
                        Dir("Rune Wardens", F("data.pak", 11.2, 116)),
                        Dir("Orbital Workshop", F("GameData.pak", 9.6, 16)),
                        Dir("Colony Depths", F("data.assets", 4.9, 67)),
                        Dir("Tiny Factory Tycoon", F("data.assets", 3.5, 255)),
                        Dir("Pixel Harbor", F("data.assets", 6.0, 150)),
                        Dir("Last Bastion", F("data.pak", 3.1, 140)),
                        Dir("Skyline Builder", F("data.assets", 2.4, 300)),
                        F("14 more games.pak", 16.6, 300, 14)),
                    Dir("downloading", F("update chunks.tmp", 12.3, 1, 640)),
                    Dir("shadercache", F("shaders.cache", 7.4, 10, 2_100)))),
                Dir("Arena Legends", Dir("data", F("content.idx", 51.1, 20)), F("ArenaLegends.exe", 1.3, 20))),
            Dir("Program Files",
                Dir("GameEngine", Dir("Editors",
                    Dir("2026.1", F("Editor.exe", 7.4, 24)), Dir("2025.3", F("Editor.exe", 7.1, 230)),
                    Dir("2024.2", F("Editor.exe", 6.8, 400)))),
                Dir("PhotoStudio Suite", F("PhotoStudio.dll", 7.9, 60)), Dir("Microsoft Office", F("root.dll", 4.6, 30)),
                Dir("VectorDraw", F("VectorDraw.exe", 3.8, 90)), Dir("NVIDIA Corporation", F("driver.dll", 3.4, 14)),
                Dir("3D Studio", F("studio.exe", 1.5, 60)), Dir("Game Launcher", F("Launcher.exe", 0.9, 30)),
                F("62 more apps.dll", 22.4, 100, 62)),
            Dir("Windows",
                Dir("WinSxS", F("active components.dll", 6.1, 12, 40_000), F("superseded components.dll", 3.1, 120, 18_000)),
                Dir("System32", Dir("DriverStore", F("drivers.sys", 4.1, 20)), F("system files.dll", 3.3, 12, 20_000)),
                Dir("Installer", F("msi cache.msi", 6.3, 60)), Dir("SoftwareDistribution", F("updates.cab", 3.1, 5)),
                F("other.dll", 5.8, 30, 30_000)),
            F("hiberfil.sys", 25.6, 0),
            Dir("$Recycle.Bin", F("deleted files.dat", 21.3, 0, 800)),
            Dir("ProgramData", Dir("Package Cache", F("installers.msi", 6.2)), Dir("Microsoft", F("data.dat", 5.1)),
                Dir("NVIDIA", F("data.dat", 2.9)), F("other.dat", 4.5)),
            F("pagefile.sys", 16.0, 0),
            Dir("System Volume Information", F("restore point (latest)", 3.6, 1), F("older restore points (4)", 10.6, 30)));

        // An empty catalog: the demo must not pick up anything installed on this PC.
        ScanPostProcessor.Run(root, TotalBytes, FreeBytes, CancellationToken.None, new GameCatalog());
        LabelGame(root, @"Program Files (x86)\Steam\steamapps\common\Ironvale Chronicles", "Steam", now.AddDays(-8));
        LabelGame(root, @"Program Files (x86)\Steam\steamapps\common\Hearthfield Kingdoms", "Steam", now.AddDays(-29));
        LabelGame(root, @"Program Files (x86)\Arena Legends", "Battle.net", null);

        var scan = new ScanResult
        {
            Root = root, DriveRoot = "C:\\", Method = ScanMethod.MasterFileTable, TotalBytes = TotalBytes, FreeBytes = FreeBytes,
            Duration = TimeSpan.FromSeconds(3.2), ScannedAt = now, ItemCount = 1_184_203,
        };

        // What the drive looked like a week ago.
        baseline = Snapshot.From(scan);
        baseline.TakenAt = now.AddDays(-7);
        baseline.UsedBytes = scan.UsedBytes - (long)(46.2 * GB);
        Adjust(baseline, @"\Users\alex\AI\llm-models", -18.6);
        Adjust(baseline, @"\Program Files (x86)\Steam\steamapps\downloading", -12.3);
        Adjust(baseline, @"\Program Files (x86)\Steam\steamapps\common\Ironvale Chronicles", -8.9);
        Adjust(baseline, @"\Users\alex\Videos\Captures", -6.4);
        Adjust(baseline, @"\Users\alex\AppData\Local\Temp", -3.1);
        Adjust(baseline, @"\$Recycle.Bin", -2.4);
        Adjust(baseline, @"\Users\alex\Downloads", +5.5);

        double[] free = [74.2, 73.8, 73.1, 72.9, 71.6, 70.8, 70.2, 69.5, 62.3, 61.8, 61.0, 60.4, 59.7, 58.9, 58.2,
                         52.6, 51.9, 51.1, 50.4, 49.6, 47.9, 46.8, 40.2, 39.5, 38.1, 36.4, 35.2, 27.6, 26.9, 18.4];
        freeSpace = free.Select((gb, i) => new FreeSpaceSample { At = now.AddDays(i - free.Length + 1), Free = (long)(gb * GB) }).ToList();
        return scan;
    }

    /// <summary>Cleanup suggestions for the demo drive, independent of this PC's Steam setup.</summary>
    public static List<Suggestion> Suggestions(ScanResult scan) => CleanupService.Analyze(scan, [SteamLibrary]);

    private static void LabelGame(DiskNode root, string path, string launcher, DateTime? played)
    {
        if (root.Find(path) is not { } node) return;
        node.Flags |= NodeFlags.Game;
        node.Label = node.Name;
        node.Tag = new GameInfo(node.Name, launcher, node.FullPath, null, null, played);
    }

    /// <summary>Changes a folder's size in the snapshot and in every parent folder.</summary>
    private static void Adjust(Snapshot s, string path, double gb)
    {
        long delta = (long)(gb * GB);
        for (var p = path; p.Length > 0; p = p[..Math.Max(0, p.LastIndexOf('\\'))])
        {
            s.Folders.TryGetValue(p, out var size);
            long next = size + delta;
            if (next >= Snapshot.MinFolderBytes) s.Folders[p] = next;
            else s.Folders.Remove(p);
        }
    }
}
