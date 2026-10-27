namespace DiskMap.Core.Model;

public enum Category : byte
{
    Other = 0,
    Games,
    AiModels,
    Video,
    Apps,
    Caches,
    Archives,
    Images,
    Documents,
    System,
}

public static class Categories
{
    public const int Count = 10;

    public static readonly Category[] All =
    [
        Category.Games, Category.AiModels, Category.Video, Category.Apps, Category.Caches,
        Category.Archives, Category.Images, Category.Documents, Category.Other, Category.System,
    ];

    public static string Name(Category c) => c switch
    {
        Category.Games => "Games",
        Category.AiModels => "AI models",
        Category.Video => "Video & audio",
        Category.Apps => "Apps",
        Category.Caches => "Caches & temp",
        Category.Archives => "Installers & archives",
        Category.Images => "Images",
        Category.Documents => "Documents",
        Category.System => "Windows & system",
        _ => "Other",
    };

    /// <summary>Category colour as 0xRRGGBB. Mid-to-high lightness so dark labels stay readable.</summary>
    public static uint Rgb(Category c) => c switch
    {
        Category.Games => 0xF2A541,
        Category.AiModels => 0xB57BFF,
        Category.Video => 0xE86A92,
        Category.Apps => 0x4C9AFF,
        Category.Caches => 0x3FC1B0,
        Category.Archives => 0xE07856,
        Category.Images => 0x9BD15B,
        Category.Documents => 0xA9B4C2,
        Category.System => 0x5E6670,
        _ => 0x8A8F98,
    };

    /// <summary>True when labels on this colour need light text.</summary>
    public static bool NeedsLightText(Category c) => c == Category.System;

    private static readonly Dictionary<string, Category> s_byExtension = Build();

    public static Category FromExtension(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1) return Category.Other;
        var ext = fileName[(dot + 1)..];
        if (ext.Length > 12) return Category.Other;
        Span<char> lower = stackalloc char[ext.Length];
        ext.ToLowerInvariant(lower);
        return s_byExtension.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(lower, out var c) ? c : Category.Other;
    }

    public static bool IsExecutableOrInstaller(ReadOnlySpan<char> fileName) =>
        fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".msix", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, Category> Build()
    {
        var d = new Dictionary<string, Category>(StringComparer.Ordinal);
        void Add(Category c, string list)
        {
            foreach (var e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) d[e] = c;
        }

        Add(Category.AiModels, "gguf ggml safetensors ckpt pt pth onnx tflite mlmodel h5 keras npz pkl lora");
        Add(Category.Video, "mp4 mkv mov avi wmv webm m4v mts m2ts flv mpg mpeg vob 3gp ts mxf braw r3d " +
                            "mp3 wav flac ogg m4a aac wma opus aiff aif alac mid midi");
        Add(Category.Images, "jpg jpeg png gif bmp tif tiff webp heic heif avif raw cr2 cr3 nef arw dng orf rw2 raf " +
                             "psd psb kra xcf ico svg exr hdr tga dds ktx");
        Add(Category.Archives, "zip 7z rar tar gz tgz bz2 xz zst lz4 cab iso img vhd vhdx vmdk qcow2 vdi wim esd dmg " +
                               "msi msix msixbundle appx appxbundle nupkg whl apk xapk");
        Add(Category.Documents, "pdf doc docx xls xlsx ppt pptx odt ods odp txt md rtf csv epub mobi pages key numbers " +
                                "blend blend1 f3d f3z dwg dxf stl 3mf step stp obj fbx gltf glb max ma mb c4d skp " +
                                "aep prproj veg kdenlive drp afphoto afdesign ai indd sketch fig xd one onepkg pst ost");
        Add(Category.Apps, "exe dll sys ocx drv cpl lib so dylib jar node pyd winmd mui");
        Add(Category.Games, "pak vpk bsa ba2 ucas utoc bundle assets resource forge rpf big bik wem bnk");
        Add(Category.Caches, "tmp temp cache dmp mdmp etl log old bak crdownload partial part download");
        return d;
    }
}
