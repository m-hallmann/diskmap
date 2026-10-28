using System.Diagnostics;
using System.Numerics;
using DiskMap.Core;
using DiskMap.Core.Layout;
using DiskMap.Core.Model;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DiskMap.Controls;

public enum MapTransition { None, Fade, Unfold, ZoomIn, ZoomOut, Morph }

public sealed class MapBoxEventArgs(MapBox box, Point position, bool add) : EventArgs
{
    public MapBox Box { get; } = box;
    public DiskNode? Node => Box.Node;
    public Point Position { get; } = position;
    public bool AddToSelection { get; } = add;
}

/// <summary>
/// Draws a folder as nested boxes (area = space used) with Win2D. Folders are quiet surfaces with a plain title;
/// colour belongs to the files, either by the big folder they belong to or by file type. The finished picture is
/// cached in a bitmap; hover outlines are drawn on top each frame. Transitions run on the composition clock.
/// </summary>
public sealed partial class TreemapControl : UserControl
{
    private readonly CanvasControl _canvas;

    private DiskNode? _folder;
    private long _freeBytes;
    private List<MapBox> _boxes = new();
    private MapBox? _hover;
    private MapLayoutOptions _options = new();
    private List<DiskNode> _groups = new();
    private Dictionary<DiskNode, int> _groupIndex = new();

    private CanvasRenderTarget? _cache;
    private bool _cacheValid;
    private Palette _pal = Palette.Dark();
    private CanvasTextFormat? _headFmt, _headSmallFmt, _sizeFmt, _tileFmt, _tileSizeFmt, _centerFmt, _centerStrongFmt, _pillFmt;
    private readonly Dictionary<int, CanvasTextFormat> _bigFmts = new();
    private CanvasImageBrush? _hatchLight, _hatchDark, _dotsLight, _dotsDark;
    private CanvasStrokeStyle? _dash;

    // Animation state
    private enum Anim { None, Fade, Zoom, Morph }
    private Anim _anim;
    private long _animStart;
    private double _animDuration;
    private bool _rendering;
    private CanvasRenderTarget? _prev;        // the picture we are leaving (fade / zoom)
    private RectD _zoomRect;                  // where the child folder sits inside the parent picture
    private bool _zoomIn;
    private Dictionary<long, RectD> _morphFrom = new();
    private List<(MapBox Box, RectD Rect)> _morphGone = new();
    private bool _unfold;

    // Frame timing, logged when DISKMAP_FRAMELOG is set (used to check animations stay smooth).
    public static bool LogFrames { get; } = Environment.GetEnvironmentVariable("DISKMAP_FRAMELOG") == "1";
    private readonly List<double> _drawMs = new();
    private readonly List<double> _gapMs = new();
    private long _lastFrame;

    public TreemapControl()
    {
        IsTabStop = true;
        _canvas = new CanvasControl { ClearColor = Color.FromArgb(0, 0, 0, 0) };
        _canvas.CreateResources += OnCreateResources;
        _canvas.Draw += OnDraw;
        Content = _canvas;

        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => SetHover(null);
        PointerPressed += OnPointerPressed;
        DoubleTapped += OnDoubleTapped;
        RightTapped += OnRightTapped;
        SizeChanged += (_, _) => Relayout(MapTransition.None);
        ActualThemeChanged += (_, _) => { UpdatePalette(); InvalidateMap(); };
        Unloaded += (_, _) => StopRendering();
        UpdatePalette();
    }

    // ---- Inputs from the page ---------------------------------------------------------------

    /// <summary>Selected items get an accent outline and a check mark.</summary>
    public IReadOnlySet<DiskNode>? Selection { get; set; }

    /// <summary>The item shown in the details pane.</summary>
    public DiskNode? FocusNode { get; set; }

    /// <summary>Items outside the current search are faded.</summary>
    public IReadOnlySet<DiskNode>? FilterHits { get; set; }

    /// <summary>Change since the last scan, shown as badges.</summary>
    public IReadOnlyDictionary<DiskNode, long>? Growth { get; set; }

    /// <summary>Colour files by the big folder they belong to, else by file type. Takes effect on the next layout.</summary>
    public bool ColorByFolder { get; set; } = true;

    /// <summary>How boxes are arranged inside a folder. Takes effect on the next layout.</summary>
    public MapTiling Tiling { get; set; } = MapTiling.Rows;

    /// <summary>The big folders the shown folder is coloured by, largest first.</summary>
    public IReadOnlyList<DiskNode> Groups => _groups;

    public IReadOnlyList<MapBox> Boxes => _boxes;

    public event EventHandler<MapBoxEventArgs>? BoxClicked;
    public event EventHandler<MapBoxEventArgs>? BoxDoubleClicked;
    public event EventHandler<MapBoxEventArgs>? BoxRightClicked;
    public event EventHandler<MapBox?>? HoverChanged;

    public DiskNode? Folder => _folder;

    /// <summary>Shows a folder, animating from what was on screen before.</summary>
    public void Show(DiskNode folder, long freeBytes, MapTransition transition, DiskNode? zoomNode = null)
    {
        var oldBoxes = _boxes;
        var oldFolder = _folder;
        _folder = folder;
        _freeBytes = freeBytes;
        BeginTransition(transition, oldBoxes, oldFolder, zoomNode);
    }

    /// <summary>Redraws after selection, focus, search or badges changed.</summary>
    public void InvalidateMap(bool fade = false)
    {
        if (fade && _cacheValid && _cache != null && _anim == Anim.None)
        {
            _prev = CopyOf(_cache);
            _cacheValid = false;
            RenderCache();
            Start(Anim.Fade, 0.16);
            return;
        }
        _cacheValid = false;
        _canvas.Invalidate();
    }

    /// <summary>Re-lays out the same folder after the tree changed (e.g. something was deleted).</summary>
    public void Relayout(MapTransition transition)
    {
        if (_folder == null) return;
        var old = _boxes;
        if (transition == MapTransition.None)
        {
            _boxes = BuildLayout();
            _cacheValid = false;
            _canvas.Invalidate();
            return;
        }
        BeginTransition(transition, old, _folder, null);
    }

    // ---- Layout and transitions -------------------------------------------------------------

    private List<MapBox> BuildLayout()
    {
        if (_folder == null) return new();
        // Groups don't depend on the size, and the details pane reads them even before the map has one.
        _groups = FolderGroups.Find(_folder);
        _groupIndex = new Dictionary<DiskNode, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _groups.Count; i++) _groupIndex[_groups[i]] = i;
        if (ActualWidth < 4 || ActualHeight < 4) return new();
        long free = _folder.Parent == null ? _freeBytes : 0;
        _options = new MapLayoutOptions { Tiling = Tiling };
        return MapLayout.Build(_folder, new RectD(0, 0, ActualWidth, ActualHeight), free, _options);
    }

    private void BeginTransition(MapTransition transition, List<MapBox> oldBoxes, DiskNode? oldFolder, DiskNode? zoomNode)
    {
        bool canAnimate = _cache != null && _cacheValid && oldBoxes.Count > 0 && ActualWidth > 4;
        _boxes = BuildLayout();
        _hover = null;

        switch (canAnimate ? transition : (transition == MapTransition.Unfold ? MapTransition.Unfold : MapTransition.None))
        {
            case MapTransition.ZoomIn when zoomNode != null && FindRect(oldBoxes, zoomNode) is { } inParent:
                _prev = CopyOf(_cache!);
                _zoomRect = inParent;
                _zoomIn = true;
                _cacheValid = false;
                RenderCache();
                Start(Anim.Zoom, 0.42);
                break;

            case MapTransition.ZoomOut when oldFolder != null && FindRect(_boxes, oldFolder) is { } inNewParent:
                _prev = CopyOf(_cache!);
                _zoomRect = inNewParent;
                _zoomIn = false;
                _cacheValid = false;
                RenderCache();
                Start(Anim.Zoom, 0.42);
                break;

            case MapTransition.Morph:
                _morphFrom = new Dictionary<long, RectD>();
                foreach (var b in oldBoxes) _morphFrom[b.Key] = b.Rect;
                var newKeys = new HashSet<long>(_boxes.Select(b => b.Key));
                _morphGone = oldBoxes.Where(b => !newKeys.Contains(b.Key)).Select(b => (b, b.Rect)).ToList();
                _unfold = false;
                PrepareMorphEnd();
                Start(Anim.Morph, 0.5);
                break;

            case MapTransition.Unfold:
                _morphFrom = new Dictionary<long, RectD>();
                _morphGone = new();
                _unfold = true;
                PrepareMorphEnd();
                Start(Anim.Morph, 0.85);
                break;

            case MapTransition.Fade:
            case MapTransition.ZoomIn:
            case MapTransition.ZoomOut:
                _prev = CopyOf(_cache!);
                _cacheValid = false;
                RenderCache();
                Start(Anim.Fade, 0.2);
                break;

            default:
                _cacheValid = false;
                _anim = Anim.None;
                _canvas.Invalidate();
                break;
        }
    }

    private static RectD? FindRect(List<MapBox> boxes, DiskNode node)
    {
        foreach (var b in boxes)
        {
            if (b.Shows(node)) return b.Rect;
        }
        return null;
    }

    private void Start(Anim anim, double seconds)
    {
        _anim = anim;
        _animDuration = seconds;
        _animStart = Stopwatch.GetTimestamp();
        if (!_rendering)
        {
            _rendering = true;
            CompositionTarget.Rendering += OnRendering;
        }
        _canvas.Invalidate();
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private double Progress => Math.Clamp(Stopwatch.GetElapsedTime(_animStart).TotalSeconds / _animDuration, 0, 1);

    private void OnRendering(object? sender, object e)
    {
        if (_anim == Anim.None)
        {
            StopRendering();
            return;
        }
        if (Progress >= 1)
        {
            if (_anim == Anim.Morph)
            {
                // Labels fade in after boxes settle. Both pictures were rendered before the morph started,
                // so the hand-off costs nothing on this frame.
                _prev = _morphEnd ?? RenderMorphFrame(1);
                _morphEnd = null;
                if (!_cacheValid) RenderCache();
                _anim = Anim.Fade;
                _animDuration = 0.14;
                _animStart = Stopwatch.GetTimestamp();
            }
            else
            {
                if (LogFrames && _drawMs.Count > 0)
                {
                    _gapMs.RemoveAt(0);
                    Services.Log.Write($"frames: {_drawMs.Count}, {_boxes.Count} boxes, draw avg {_drawMs.Average():0.0} ms max {_drawMs.Max():0.0} ms, " +
                                       $"interval avg {(_gapMs.Count > 0 ? _gapMs.Average() : 0):0.0} ms max {(_gapMs.Count > 0 ? _gapMs.Max() : 0):0.0} ms");
                    _drawMs.Clear();
                    _gapMs.Clear();
                }
                _anim = Anim.None;
                _prev?.Dispose();
                _prev = null;
            }
        }
        _canvas.Invalidate();
    }

    // Fluent "decelerate" feel.
    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 4);
    private static double EaseInOut(double t) => t < 0.5 ? 8 * t * t * t * t : 1 - Math.Pow(-2 * t + 2, 4) / 2;
    private static double Smooth(double a, double b, double t) => Math.Clamp((t - a) / (b - a), 0, 1);

    // ---- Drawing ----------------------------------------------------------------------------

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _hatchLight = Pattern(sender, 6, Color.FromArgb(20, 255, 255, 255), (ds, c) =>
        {
            ds.DrawLine(-1, 7, 7, -1, c, 1f);
            ds.DrawLine(-1, 1, 1, -1, c, 1f);
            ds.DrawLine(5, 7, 7, 5, c, 1f);
        });
        _hatchDark = Pattern(sender, 6, Color.FromArgb(20, 0, 0, 0), (ds, c) =>
        {
            ds.DrawLine(-1, 7, 7, -1, c, 1f);
            ds.DrawLine(-1, 1, 1, -1, c, 1f);
            ds.DrawLine(5, 7, 7, 5, c, 1f);
        });
        _dotsLight = Pattern(sender, 5, Color.FromArgb(41, 255, 255, 255), (ds, c) => ds.FillCircle(2.5f, 2.5f, 0.9f, c));
        _dotsDark = Pattern(sender, 5, Color.FromArgb(41, 0, 0, 0), (ds, c) => ds.FillCircle(2.5f, 2.5f, 0.9f, c));
        _dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };

        _headFmt = Fmt(12.5f, Microsoft.UI.Text.FontWeights.SemiBold);
        _headSmallFmt = Fmt(12, Microsoft.UI.Text.FontWeights.SemiBold);
        _sizeFmt = Fmt(11.5f, Microsoft.UI.Text.FontWeights.Normal);
        _tileFmt = Fmt(12, Microsoft.UI.Text.FontWeights.SemiBold);
        _tileSizeFmt = Fmt(11.5f, Microsoft.UI.Text.FontWeights.Normal);
        _centerFmt = Fmt(12, Microsoft.UI.Text.FontWeights.Normal, CanvasHorizontalAlignment.Center);
        _centerStrongFmt = Fmt(12, Microsoft.UI.Text.FontWeights.SemiBold, CanvasHorizontalAlignment.Center);
        _pillFmt = Fmt(11, Microsoft.UI.Text.FontWeights.SemiBold, CanvasHorizontalAlignment.Center);
        _bigFmts.Clear();
        _cache?.Dispose();
        _cache = null;
        _cacheValid = false;
    }

    private static CanvasImageBrush Pattern(CanvasControl sender, int size, Color color, Action<CanvasDrawingSession, Color> draw)
    {
        var tile = new CanvasRenderTarget(sender, size, size);
        using (var ds = tile.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(0, 0, 0, 0));
            draw(ds, color);
        }
        return new CanvasImageBrush(sender, tile) { ExtendX = CanvasEdgeBehavior.Wrap, ExtendY = CanvasEdgeBehavior.Wrap };
    }

    private static CanvasTextFormat Fmt(float size, Windows.UI.Text.FontWeight weight,
        CanvasHorizontalAlignment align = CanvasHorizontalAlignment.Left, string family = "Segoe UI Variable Text") => new()
    {
        FontFamily = family,
        FontSize = size,
        FontWeight = weight,
        HorizontalAlignment = align,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap,
        TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        TrimmingSign = CanvasTrimmingSign.Ellipsis,
    };

    /// <summary>Large light figures for the size of big boxes.</summary>
    private CanvasTextFormat BigFmt(int size)
    {
        if (!_bigFmts.TryGetValue(size, out var fmt))
        {
            fmt = Fmt(size, Microsoft.UI.Text.FontWeights.SemiLight, family: "Segoe UI Variable Display");
            _bigFmts[size] = fmt;
        }
        return fmt;
    }

    private void UpdatePalette()
    {
        _pal = ActualTheme == ElementTheme.Light ? Palette.Light() : Palette.Dark();
    }

    private CanvasRenderTarget CopyOf(CanvasRenderTarget source)
    {
        var copy = new CanvasRenderTarget(_canvas, source.Size);
        using var ds = copy.CreateDrawingSession();
        ds.Clear(Color.FromArgb(0, 0, 0, 0));
        ds.DrawImage(source);
        return copy;
    }

    private void RenderCache()
    {
        var size = new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        if (_cache == null || _cache.Size != size || _cache.Dpi != _canvas.Dpi)
        {
            _cache?.Dispose();
            _cache = new CanvasRenderTarget(_canvas, (float)size.Width, (float)size.Height);
        }
        using (var ds = _cache.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(0, 0, 0, 0));
            DrawBackground(ds, size);
            DrawBoxes(ds, _boxes, null, labels: true);
            DrawSelection(ds, _boxes);
        }
        _cacheValid = true;
    }

    private CanvasRenderTarget? _morphEnd;

    private void PrepareMorphEnd()
    {
        if (ActualWidth < 4 || _hatchLight == null)
        {
            _cacheValid = false;
            return;
        }
        RenderCache();
        _morphEnd?.Dispose();
        _morphEnd = RenderMorphFrame(1);
    }

    private CanvasRenderTarget RenderMorphFrame(double t)
    {
        var target = new CanvasRenderTarget(_canvas, (float)Math.Max(1, ActualWidth), (float)Math.Max(1, ActualHeight));
        using var ds = target.CreateDrawingSession();
        ds.Clear(Color.FromArgb(0, 0, 0, 0));
        DrawMorph(ds, t);
        return target;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_hatchLight == null) return;
        if (!LogFrames || _anim == Anim.None)
        {
            Draw(args.DrawingSession);
            return;
        }
        long start = Stopwatch.GetTimestamp();
        if (_lastFrame != 0) _gapMs.Add(Stopwatch.GetElapsedTime(_lastFrame, start).TotalMilliseconds);
        _lastFrame = start;
        Draw(args.DrawingSession);
        _drawMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    private void Draw(CanvasDrawingSession ds)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);

        switch (_anim)
        {
            case Anim.Zoom when _prev != null && _cache != null:
            {
                if (!_cacheValid) RenderCache();
                double p = EaseInOut(Progress);
                var fullR = new RectD(0, 0, full.Width, full.Height);
                // "cur" is where the child folder's picture sits on screen right now.
                var cur = _zoomIn ? RectD.Lerp(_zoomRect, fullR, p) : RectD.Lerp(fullR, _zoomRect, p);
                var parent = _zoomIn ? _prev : _cache;
                var child = _zoomIn ? _cache : _prev;
                DrawBackground(ds, full.Size());
                DrawMapped(ds, parent, _zoomRect, cur, 1f);
                float childAlpha = (float)(_zoomIn ? Smooth(0.05, 0.55, p) : 1 - Smooth(0.35, 0.95, p));
                DrawMapped(ds, child, fullR, cur, childAlpha);
                return;
            }
            case Anim.Fade when _prev != null:
            {
                if (!_cacheValid) RenderCache();
                double p = EaseOut(Progress);
                ds.DrawImage(_prev, full, full, (float)(1 - p));
                ds.DrawImage(_cache!, full, full, (float)p);
                DrawHover(ds);
                return;
            }
            case Anim.Morph:
                DrawMorph(ds, Progress);
                return;
        }

        if (!_cacheValid || _cache == null) RenderCache();
        ds.DrawImage(_cache!);
        DrawHover(ds);
    }

    private static void DrawMapped(CanvasDrawingSession ds, CanvasRenderTarget image, RectD from, RectD to, float opacity)
    {
        if (opacity <= 0.001f || from.W <= 0 || from.H <= 0) return;
        double sx = to.W / from.W, sy = to.H / from.H;
        var size = image.Size;
        var dest = new Rect(to.X - from.X * sx, to.Y - from.Y * sy, size.Width * sx, size.Height * sy);
        ds.DrawImage(image, dest, new Rect(0, 0, size.Width, size.Height), opacity, CanvasImageInterpolation.Linear);
    }

    private void DrawMorph(CanvasDrawingSession ds, double t)
    {
        DrawBackground(ds, new Size(ActualWidth, ActualHeight));
        double e = EaseOut(t);
        foreach (var (box, rect) in _morphGone)
        {
            double a = 1 - Smooth(0, 0.6, t);
            if (a <= 0) continue;
            var shrink = Scale(rect, 1 - 0.25 * e);
            DrawBox(ds, box, shrink, (float)a, labels: false);
        }
        DrawBoxes(ds, _boxes, b =>
        {
            if (_unfold)
            {
                double local = Smooth(b.Depth * 0.07, b.Depth * 0.07 + 0.55, t);
                double le = EaseOut(local);
                return (Scale(b.Rect, 0.82 + 0.18 * le), (float)le);
            }
            if (_morphFrom.TryGetValue(b.Key, out var from)) return (RectD.Lerp(from, b.Rect, e), 1f);
            return (Scale(b.Rect, 0.9 + 0.1 * e), (float)Smooth(0.2, 1, t));
        }, labels: false);
    }

    private static RectD Scale(RectD r, double s)
    {
        double w = r.W * s, h = r.H * s;
        return new RectD(r.X + (r.W - w) / 2, r.Y + (r.H - h) / 2, w, h);
    }

    private void DrawBackground(CanvasDrawingSession ds, Size size)
    {
        var r = new Rect(0, 0, size.Width, size.Height);
        ds.FillRoundedRectangle(r, 10, 10, _pal.Background);
        if (!_pal.IsDark) ds.DrawRoundedRectangle(new Rect(0.5, 0.5, Math.Max(0, size.Width - 1), Math.Max(0, size.Height - 1)), 10, 10, _pal.Edge, 1f);
    }

    private void DrawBoxes(CanvasDrawingSession ds, List<MapBox> boxes, Func<MapBox, (RectD Rect, float Alpha)>? animate, bool labels)
    {
        foreach (var box in boxes)
        {
            var (rect, alpha) = animate?.Invoke(box) ?? (box.Rect, 1f);
            if (alpha <= 0.01f || rect.W < 0.5 || rect.H < 0.5) continue;
            DrawBox(ds, box, rect, alpha, labels);
        }
    }

    private bool IsDimmed(MapBox box) => FilterHits != null && (box.Node == null || !FilterHits.Contains(box.Node));

    private bool IsSelected(MapBox box) =>
        Selection != null && (box.Node != null && Selection.Contains(box.Node) || (box.Chain?.Any(Selection.Contains) ?? false));

    private void DrawBox(CanvasDrawingSession ds, MapBox box, RectD r, float alpha, bool labels)
    {
        if (IsDimmed(box)) alpha *= 0.2f;
        switch (box.Kind)
        {
            case BoxKind.Frame: DrawFrame(ds, box, r, alpha, labels); break;
            case BoxKind.Tile: DrawTile(ds, box, r, alpha, labels); break;
            case BoxKind.Free: DrawFree(ds, box, r, alpha, labels); break;
            case BoxKind.Rest: DrawRest(ds, box, r, alpha, labels); break;
        }
    }

    /// <summary>A folder: a faint surface with a hairline edge and a plain title, no coloured bar.</summary>
    private void DrawFrame(CanvasDrawingSession ds, MapBox box, RectD r, float alpha, bool labels)
    {
        var node = box.Node!;
        int group = GroupOf(node);
        float radius = FrameRadius(box.Depth);
        var rr = new Rect(r.X + 0.5, r.Y + 0.5, Math.Max(0, r.W - 1), Math.Max(0, r.H - 1));
        ds.FillRoundedRectangle(rr, radius, radius, WithAlpha(group >= 0 ? MapColors.GroupWash(group, _pal.IsDark) : _pal.Surface, alpha));
        ds.DrawRoundedRectangle(rr, radius, radius, WithAlpha(group >= 0 ? MapColors.GroupEdge(group, _pal.IsDark) : _pal.Edge, alpha), 1f,
            node.IsProtected ? _dash : null);
        if (!labels || r.W < 28) return;

        double head = _options.HeaderHeight(box.Depth), pad = _options.Padding(box.Depth);
        float cy = (float)(r.Y + head / 2);
        double x = r.X + pad + 3, right = r.Right - pad - 4;
        // The check mark leads the title, so nothing else in it moves when the folder is selected.
        if (IsSelected(box))
        {
            DrawCheck(ds, new Vector2((float)(x + 7), cy), alpha);
            x += 20;
        }
        if (group >= 0 && _groupIndex.ContainsKey(node))
        {
            ds.FillCircle((float)(x + 4), cy, 4, WithAlpha(MapColors.GroupDot(group, _pal.IsDark), alpha));
            x += 14;
        }
        if (r.W >= 120)
        {
            string size = Format.Bytes(box.Size);
            float width = Measure(ds, size, _sizeFmt!);
            ds.DrawText(size, new Rect(right - width, r.Y, width + 2, head), WithAlpha(_pal.TextTertiary, alpha), _sizeFmt);
            right -= width + 6;
        }

        string prefix = box.Chain is { Count: > 1 } chain ? string.Join(" / ", chain.Take(chain.Count - 1).Select(n => n.DisplayName)) + " / " : "";
        var fmt = box.Depth < 2 ? _headFmt! : _headSmallFmt!;
        if (DeltaOf(box) is { } delta)
        {
            // The name wins: the change shows as a pill when there is room, otherwise as a dot.
            string text = Format.Delta(delta);
            float pill = Measure(ds, text, _pillFmt!) + 12;
            if (right - x >= Measure(ds, prefix + node.DisplayName, fmt) + pill + 6)
            {
                DrawPill(ds, right - pill, cy, text, delta, alpha, onTile: false);
                right -= pill + 6;
            }
            else if (right - x >= 40)
            {
                DrawGrowthDot(ds, (float)(right - 4), cy, delta, alpha, ring: false);
                right -= 13;
            }
        }
        DrawTitle(ds, prefix, node.DisplayName, x, r.Y, right - x, head, fmt, alpha);
    }

    private void DrawTitle(CanvasDrawingSession ds, string prefix, string name, double x, double y, double width, double height,
        CanvasTextFormat fmt, float alpha)
    {
        if (width < 12) return;
        using var layout = new CanvasTextLayout(ds, prefix + name, fmt, (float)width, (float)height);
        if (prefix.Length > 0) layout.SetColor(0, prefix.Length, WithAlpha(_pal.TextTertiary, alpha));
        ds.DrawTextLayout(layout, (float)x, (float)y, WithAlpha(_pal.Text, alpha));
    }

    /// <summary>A file (or a folder too small to open): flat colour, its name, and its size as a large figure when there is room.</summary>
    private void DrawTile(CanvasDrawingSession ds, MapBox box, RectD r, float alpha, bool labels)
    {
        var node = box.Node!;
        var (fill, darkInk) = TileColor(box);
        float radius = (float)Math.Min(5, Math.Min(r.W, r.H) / 4);
        var rect = new Rect(r.X, r.Y, r.W, r.H);
        ds.FillRoundedRectangle(rect, radius, radius, WithAlpha(fill, alpha));
        if (_pal.IsDark)
        {
            if (r.W > 2 * radius + 2) ds.DrawLine((float)(r.X + radius), (float)r.Y + 0.5f, (float)(r.Right - radius), (float)r.Y + 0.5f, WithAlpha(_pal.TopLight, alpha), 1f);
        }
        else if (r.W >= 4 && r.H >= 4)
        {
            ds.DrawRoundedRectangle(new Rect(r.X + 0.5, r.Y + 0.5, r.W - 1, r.H - 1), radius, radius, WithAlpha(_pal.TopLight, alpha), 1f);
        }
        if (node.IsProtected && r.W * r.H >= 64)
        {
            var hatch = darkInk ? _hatchDark! : _hatchLight!;
            hatch.Opacity = alpha;
            ds.FillRoundedRectangle(rect, radius, radius, hatch);
        }
        if (!labels) return;

        long? delta = Growth != null && Growth.TryGetValue(node, out var d) ? d : null;
        if (r.W < 44 || r.H < 22)
        {
            if (delta is { } small && r.W >= 18 && r.H >= 18) DrawGrowthDot(ds, (float)(r.Right - 7), (float)(r.Y + 7), small, alpha, ring: true);
            return;
        }

        var ink = darkInk ? Color.FromArgb(214, 0, 0, 0) : Color.FromArgb(240, 255, 255, 255);
        double x = r.X + 7, y = r.Y + 5;
        if (IsSelected(box))
        {
            DrawCheck(ds, new Vector2((float)(x + 7), (float)(y + 8)), alpha);
            x += 20;
        }
        ds.DrawText(node.DisplayName, new Rect(x, y, Math.Max(0, r.Right - 7 - x), 16), WithAlpha(ink, alpha), _tileFmt);

        string size = Format.Bytes(node.Size);
        if (r.W >= 96 && r.H >= 80)
        {
            // Big boxes show their size as a large light figure: the eye finds the heavy items first.
            var fmt = BigFmt(BigFigureSize(r));
            int px = (int)fmt.FontSize;
            float lineH = px * 1.25f;
            double top = r.Bottom - 5 - lineH;
            ds.DrawText(size, new Rect(r.X + 7, top, r.W - 14, lineH), WithAlpha(ink, alpha * 0.92f), fmt);
            if (delta is { } big)
            {
                float width = Measure(ds, size, fmt), pill = Measure(ds, Format.Delta(big), _pillFmt!) + 12;
                if (7 + width + 6 + pill <= r.W - 7) DrawPill(ds, r.X + 7 + width + 6, (float)(top + lineH / 2), Format.Delta(big), big, alpha, onTile: true);
                else if (top - 20 > y + 16) DrawPill(ds, r.X + 7, (float)(top - 10), Format.Delta(big), big, alpha, onTile: true);
                else DrawGrowthDot(ds, (float)(r.Right - 7), (float)(r.Y + 7), big, alpha, ring: true);
            }
        }
        else if (r.H >= 40)
        {
            ds.DrawText(size, new Rect(r.X + 7, y + 17, Math.Max(0, r.W - 14), 16), WithAlpha(ink, alpha * 0.68f), _tileSizeFmt);
            if (delta is { } mid)
            {
                float width = Measure(ds, size, _tileSizeFmt!), pill = Measure(ds, Format.Delta(mid), _pillFmt!) + 12;
                if (7 + width + 6 + pill <= r.W - 7) DrawPill(ds, r.X + 7 + width + 6, (float)(y + 25), Format.Delta(mid), mid, alpha, onTile: true);
                else DrawGrowthDot(ds, (float)(r.Right - 7), (float)(r.Y + 7), mid, alpha, ring: true);
            }
        }
        else if (delta is { } low)
        {
            DrawGrowthDot(ds, (float)(r.Right - 7), (float)(r.Y + 7), low, alpha, ring: true);
        }
    }

    private void DrawFree(CanvasDrawingSession ds, MapBox box, RectD r, float alpha, bool labels)
    {
        var rr = new Rect(r.X + 0.5, r.Y + 0.5, Math.Max(0, r.W - 1), Math.Max(0, r.H - 1));
        ds.DrawRoundedRectangle(rr, 6, 6, WithAlpha(_pal.TextTertiary, alpha), 1f, _dash);
        if (!labels) return;
        string size = Format.Bytes(box.Size);
        if (r.W >= 96 && r.H >= 80)
        {
            // Laid out like a big file: name at the top, size as a large figure at the bottom.
            ds.DrawText("Free space", new Rect(r.X + 8, r.Y + 6, r.W - 16, 16), WithAlpha(_pal.TextSecondary, alpha), _tileFmt);
            int px = BigFigureSize(r);
            float lineH = px * 1.25f;
            ds.DrawText(size, new Rect(r.X + 8, r.Bottom - 6 - lineH, r.W - 16, lineH), WithAlpha(_pal.TextSecondary, alpha), BigFmt(px));
        }
        else if (r.W >= 60 && r.H >= 34)
        {
            DrawCentered(ds, r, "Free space", size, alpha);
        }
    }

    private static int BigFigureSize(RectD r) => (int)Math.Round(Math.Min(34, Math.Min((r.W - 14) / 4.2, Math.Max(20, Math.Sqrt(r.W * r.H) / 7.5))));

    /// <summary>Many small items: a soft dotted box, so a long tail of little files reads as one calm area.</summary>
    private void DrawRest(CanvasDrawingSession ds, MapBox box, RectD r, float alpha, bool labels)
    {
        float radius = (float)Math.Min(4, Math.Min(r.W, r.H) / 4);
        var rect = new Rect(r.X, r.Y, r.W, r.H);
        ds.FillRoundedRectangle(rect, radius, radius, WithAlpha(_pal.Rest, alpha));
        if (r.W * r.H >= 64)
        {
            var dots = _pal.IsDark ? _dotsLight! : _dotsDark!;
            dots.Opacity = alpha;
            ds.FillRoundedRectangle(rect, radius, radius, dots);
        }
        if (labels && r.W >= 70 && r.H >= 30)
        {
            DrawCentered(ds, r, $"{box.RestCount:N0} small item{(box.RestCount == 1 ? "" : "s")}", Format.Bytes(box.Size), alpha);
        }
    }

    private void DrawCentered(CanvasDrawingSession ds, RectD r, string title, string detail, float alpha)
    {
        double cy = r.Y + r.H / 2;
        ds.DrawText(title, new Rect(r.X + 4, cy - 17, Math.Max(0, r.W - 8), 16), WithAlpha(_pal.TextSecondary, alpha), _centerStrongFmt);
        ds.DrawText(detail, new Rect(r.X + 4, cy + 1, Math.Max(0, r.W - 8), 16), WithAlpha(_pal.TextTertiary, alpha), _centerFmt);
    }

    private void DrawPill(CanvasDrawingSession ds, double x, float cy, string text, long delta, float alpha, bool onTile)
    {
        float width = Measure(ds, text, _pillFmt!) + 12;
        var r = new Rect(x, cy - 8, width, 16);
        var back = onTile ? _pal.PillBack : delta >= 0 ? _pal.UpWash : _pal.DownWash;
        ds.FillRoundedRectangle(r, 5, 5, WithAlpha(back, alpha));
        ds.DrawText(text, r, WithAlpha(delta >= 0 ? _pal.Up : _pal.Down, alpha), _pillFmt);
    }

    /// <param name="ring">On a coloured tile: a ring in the map's background colour keeps the dot visible on any hue.</param>
    private void DrawGrowthDot(CanvasDrawingSession ds, float x, float y, long delta, float alpha, bool ring)
    {
        if (ring) ds.FillCircle(x, y, 5.5f, WithAlpha(_pal.Background, alpha));
        ds.FillCircle(x, y, 3.5f, WithAlpha(delta >= 0 ? _pal.Up : _pal.Down, alpha));
    }

    private void DrawCheck(CanvasDrawingSession ds, Vector2 c, float alpha)
    {
        ds.FillCircle(c, 7, WithAlpha(_pal.Accent, alpha));
        var ink = WithAlpha(_pal.OnAccent, alpha);
        ds.DrawLine(c + new Vector2(-3.2f, 0.2f), c + new Vector2(-0.8f, 2.6f), ink, 1.6f);
        ds.DrawLine(c + new Vector2(-0.8f, 2.6f), c + new Vector2(3.4f, -2.4f), ink, 1.6f);
    }

    private static float Measure(ICanvasResourceCreator rc, string text, CanvasTextFormat fmt)
    {
        using var layout = new CanvasTextLayout(rc, text, fmt, 10_000, 100);
        return (float)layout.LayoutBounds.Width;
    }

    private void DrawSelection(CanvasDrawingSession ds, List<MapBox> boxes)
    {
        foreach (var box in boxes)
        {
            bool selected = IsSelected(box);
            bool focused = FocusNode != null && box.Shows(FocusNode);
            if (!selected && !focused) continue;
            var r = box.Rect;
            float radius = RadiusOf(box, r);
            var rr = new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.W - 2), Math.Max(0, r.H - 2));
            ds.DrawRoundedRectangle(rr, radius, radius, selected ? _pal.Accent : _pal.Text, 2f);
            // Frames and labelled tiles carry the check mark before their name; small boxes get it in the corner.
            bool leading = box.Kind == BoxKind.Frame && r.W >= 28 || box.Kind == BoxKind.Tile && r.W >= 44 && r.H >= 22;
            if (selected && !leading && r.W >= 24 && r.H >= 24) DrawCheck(ds, new Vector2((float)(r.Right - 10), (float)(r.Y + 10)), 1f);
        }
    }

    private void DrawHover(CanvasDrawingSession ds)
    {
        if (_hover == null) return;
        for (var p = _hover.Parent; p != null; p = p.Parent)
        {
            var pr = p.Rect;
            float pRadius = RadiusOf(p, pr);
            ds.DrawRoundedRectangle(new Rect(pr.X + 0.5, pr.Y + 0.5, pr.W - 1, pr.H - 1), pRadius, pRadius, _pal.TextTertiary, 1f);
        }
        var r = _hover.Rect;
        float radius = RadiusOf(_hover, r);
        if (_hover.Kind != BoxKind.Frame) ds.FillRoundedRectangle(new Rect(r.X, r.Y, r.W, r.H), radius, radius, _pal.HoverWash);
        ds.DrawRoundedRectangle(new Rect(r.X + 0.75, r.Y + 0.75, Math.Max(0, r.W - 1.5), Math.Max(0, r.H - 1.5)), radius, radius, _pal.Text, 1.5f);
    }

    private static float FrameRadius(int depth) => depth switch { 0 => 9, 1 => 7, 2 => 5, _ => 4 };

    private static float RadiusOf(MapBox box, RectD r) => box.Kind switch
    {
        BoxKind.Frame => FrameRadius(box.Depth),
        BoxKind.Free => 6,
        BoxKind.Rest => (float)Math.Min(4, Math.Min(r.W, r.H) / 4),
        _ => (float)Math.Min(5, Math.Min(r.W, r.H) / 4),
    };

    /// <summary>Index of the folder group a node belongs to, or -1 (also when colouring by file type).</summary>
    private int GroupOf(DiskNode? node)
    {
        if (!ColorByFolder) return -1;
        for (var n = node; n != null && !ReferenceEquals(n, _folder); n = n.Parent)
        {
            if (_groupIndex.TryGetValue(n, out int i)) return i;
        }
        return -1;
    }

    private (Color Fill, bool DarkInk) TileColor(MapBox box)
    {
        if (!ColorByFolder) return MapColors.ForCategory(box.Node!.Category, box.Rank, _pal.IsDark);
        int group = GroupOf(box.Node);
        return group >= 0 ? MapColors.Group(group, box.Rank, _pal.IsDark) : MapColors.Neutral(box.Rank, _pal.IsDark);
    }

    /// <summary>Change since the last scan for a box, looking through a merged title from the inside out.</summary>
    private long? DeltaOf(MapBox box)
    {
        if (Growth == null || box.Node == null) return null;
        if (Growth.TryGetValue(box.Node, out var delta)) return delta;
        if (box.Chain == null) return null;
        for (int i = box.Chain.Count - 2; i >= 0; i--)
        {
            if (Growth.TryGetValue(box.Chain[i], out delta)) return delta;
        }
        return null;
    }

    private static Color WithAlpha(Color c, float alpha) => Color.FromArgb((byte)Math.Clamp(c.A * alpha, 0, 255), c.R, c.G, c.B);

    // ---- Input ------------------------------------------------------------------------------

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        SetHover(_anim == Anim.None ? MapLayout.HitTest(_boxes, p.X, p.Y) : null);
    }

    private void SetHover(MapBox? box)
    {
        if (ReferenceEquals(box, _hover)) return;
        _hover = box;
        ProtectedCursor = box?.Node != null ? InputSystemCursor.Create(InputSystemCursorShape.Hand) : null;
        HoverChanged?.Invoke(this, box);
        _canvas.Invalidate();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;
        var box = MapLayout.HitTest(_boxes, pt.Position.X, pt.Position.Y);
        if (box == null) return;
        bool add = (e.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)) != 0;
        BoxClicked?.Invoke(this, new MapBoxEventArgs(box, pt.Position, add));
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var p = e.GetPosition(this);
        var box = MapLayout.HitTest(_boxes, p.X, p.Y);
        if (box != null) BoxDoubleClicked?.Invoke(this, new MapBoxEventArgs(box, p, false));
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var p = e.GetPosition(this);
        var box = MapLayout.HitTest(_boxes, p.X, p.Y);
        if (box != null) BoxRightClicked?.Invoke(this, new MapBoxEventArgs(box, p, false));
    }

    private readonly record struct Palette(bool IsDark, Color Background, Color Surface, Color Edge, Color TopLight, Color Text,
        Color TextSecondary, Color TextTertiary, Color Rest, Color HoverWash, Color Accent, Color OnAccent,
        Color Up, Color UpWash, Color Down, Color DownWash, Color PillBack)
    {
        private static Color Hex(uint rgb, byte a = 255) => Color.FromArgb(a, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static Color SystemAccent(UIColorType type, uint fallback)
        {
            try { return new UISettings().GetColorValue(type); }
            catch (Exception) { return Hex(fallback); }
        }

        public static Palette Dark() => new(true, Hex(0x1A1A1A), Hex(0xFFFFFF, 9), Hex(0xFFFFFF, 19), Hex(0xFFFFFF, 41), Hex(0xFFFFFF, 235),
            Hex(0xFFFFFF, 163), Hex(0xFFFFFF, 117), Hex(0xFFFFFF, 13), Hex(0xFFFFFF, 20), SystemAccent(UIColorType.AccentLight2, 0x60CDFF), Hex(0x000000),
            Hex(0xF5D35B), Hex(0xF5D35B, 36), Hex(0x80D676), Hex(0x80D676, 36), Hex(0x181818, 209));

        public static Palette Light() => new(false, Hex(0xFFFFFF), Hex(0x000000, 8), Hex(0x000000, 20), Hex(0x000000, 13), Hex(0x000000, 227),
            Hex(0x000000, 158), Hex(0x000000, 138), Hex(0x000000, 10), Hex(0xFFFFFF, 56), SystemAccent(UIColorType.AccentDark1, 0x005FB8), Hex(0xFFFFFF),
            Hex(0x8A5100), Hex(0x9D5D00, 28), Hex(0x0E6E0E), Hex(0x0F7B0F, 26), Hex(0xFFFFFF, 235));
    }
}

internal static class RectExtensions
{
    public static Size Size(this Rect r) => new(r.Width, r.Height);
}
