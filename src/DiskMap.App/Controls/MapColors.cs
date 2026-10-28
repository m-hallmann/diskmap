using DiskMap.Core.Model;
using Windows.UI;

namespace DiskMap.Controls;

/// <summary>
/// Colours of the map, defined in OKLCH so every hue has the same lightness and similar strength.
/// Within a folder the biggest item is a shade deeper and the smallest a shade lighter, which keeps
/// neighbours apart without outlines.
/// </summary>
internal static class MapColors
{
    /// <summary>Hue and chroma for the folder groups, biggest group first.</summary>
    private static readonly (double H, double C)[] s_groups =
        [(255, 0.12), (72, 0.13), (305, 0.14), (175, 0.10), (32, 0.13), (135, 0.13), (350, 0.12), (215, 0.10)];

    public static (Color Fill, bool DarkInk) Group(int group, double rank, bool dark)
    {
        var (h, c) = s_groups[group % s_groups.Length];
        return Tone((dark ? 0.74 : 0.78) + (rank - 0.5) * 0.08, c, h);
    }

    /// <summary>Files outside every folder group.</summary>
    public static (Color Fill, bool DarkInk) Neutral(double rank, bool dark) => Tone((dark ? 0.47 : 0.86) + (rank - 0.5) * 0.05, 0.006, 250);

    public static (Color Fill, bool DarkInk) ForCategory(Category category, double rank, bool dark)
    {
        var (h, c) = CategoryHue(category);
        double l = category switch
        {
            Category.System => dark ? 0.43 : 0.87,
            Category.Other => dark ? 0.56 : 0.80,
            _ => dark ? 0.74 : 0.78,
        };
        return Tone(l + (rank - 0.5) * 0.08, c, h);
    }

    /// <summary>A faint wash of the group's hue over a folder's area.</summary>
    public static Color GroupWash(int group, bool dark) => Hued(group, dark ? 0.74 : 0.62, dark ? 0.055 : 0.05);

    public static Color GroupEdge(int group, bool dark) => Hued(group, dark ? 0.74 : 0.60, dark ? 0.18 : 0.25);

    public static Color GroupDot(int group, bool dark) => Hued(group, dark ? 0.76 : 0.66, 1);

    /// <summary>The group's colour for legend swatches outside the map.</summary>
    public static Color GroupSwatch(int group) => Hued(group, 0.76, 1);

    /// <summary>Swatch for items outside every folder group; a mid grey that reads in both themes.</summary>
    public static Color NeutralSwatch { get; } = Oklch(0.6, 0.006, 250);

    private static Color Hued(int group, double l, double alpha)
    {
        var (h, c) = s_groups[group % s_groups.Length];
        return Oklch(l, c, h, alpha);
    }

    private static (Color, bool) Tone(double l, double c, double h) => (Oklch(l, c, h), l >= 0.62);

    private static (double H, double C) CategoryHue(Category c) => c switch
    {
        Category.Games => (72, 0.13),
        Category.AiModels => (302, 0.15),
        Category.Video => (356, 0.13),
        Category.Apps => (252, 0.12),
        Category.Caches => (185, 0.10),
        Category.Archives => (38, 0.13),
        Category.Images => (135, 0.13),
        Category.Documents => (250, 0.03),
        Category.System => (250, 0.012),
        _ => (250, 0.01),
    };

    public static Color Oklch(double l, double c, double h, double alpha = 1)
    {
        double hr = h * Math.PI / 180, a = c * Math.Cos(hr), b = c * Math.Sin(hr);
        double l_ = l + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = l - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = l - 0.0894841775 * a - 1.2914855480 * b;
        double lc = l_ * l_ * l_, mc = m_ * m_ * m_, sc = s_ * s_ * s_;
        double r = 4.0767416621 * lc - 3.3077115913 * mc + 0.2309699292 * sc;
        double g = -1.2684380046 * lc + 2.6097574011 * mc - 0.3413193965 * sc;
        double bl = -0.0041960863 * lc - 0.7034186147 * mc + 1.7076147010 * sc;
        return Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), Gamma(r), Gamma(g), Gamma(bl));
    }

    private static byte Gamma(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return (byte)Math.Round(255 * (x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055));
    }
}
