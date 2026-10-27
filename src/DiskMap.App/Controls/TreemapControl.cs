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
/// Draws a folder as nested boxes (area = space used) with Win2D. The finished picture is cached in a
/// bitmap; hover outlines are drawn on top each frame. Transitions run on the composition clock.
/// </summary>
public sealed partial class TreemapControl : UserControl
{
    private readonly CanvasControl _canvas;

    private DiskNode? _folder;
    private long _freeBytes;
    private List<MapBox> _boxes = new();
    private MapBox? _hover;

    private CanvasRenderTarget? _cache;
    private bool _cacheValid;
    private Palette _pal = Palette.Dark();
    private CanvasTextFormat? _headFmt, _headSizeFmt, _tileFmt, _tileSizeFmt, _centerFmt, _badgeFmt;
    private CanvasLinearGradientBrush? _cushion;
    private CanvasImageBrush? _hatch;
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
        if (_folder == null || ActualWidth < 4 || ActualHeight < 4) return new();
        long free = _folder.Parent == null ? _freeBytes : 0;
        return MapLayout.Build(_folder, new RectD(0, 0, ActualWidth, ActualHeight), free);
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
            if (ReferenceEquals(b.Node, node)) return b.Rect;
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
        _cushion = new CanvasLinearGradientBrush(sender,
        [
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(82, 255, 255, 255) },
            new CanvasGradientStop { Position = 0.45f, Color = Color.FromArgb(0, 255, 255, 255) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(46, 0, 0, 0) },
        ]);
        var hatch = new CanvasRenderTarget(sender, 8, 8);
        using (var ds = hatch.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(0, 0, 0, 0));
            var line = Color.FromArgb(34, 255, 255, 255);
            ds.DrawLine(-2, 10, 10, -2, line, 2.5f);
            ds.DrawLine(-2, 2, 2, -2, line, 2.5f);
            ds.DrawLine(6, 10, 10, 6, line, 2.5f);
        }
        _hatch = new CanvasImageBrush(sender, hatch) { ExtendX = CanvasEdgeBehavior.Wrap, ExtendY = CanvasEdgeBehavior.Wrap };
        _dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };

        _headFmt = Fmt(11, Microsoft.UI.Text.FontWeights.SemiBold, CanvasHorizontalAlignment.Left);
        _headSizeFmt = Fmt(11, Microsoft.UI.Text.FontWeights.Normal, CanvasHorizontalAlignment.Right);
        _tileFmt = Fmt(11, Microsoft.UI.Text.FontWeights.SemiBold, CanvasHorizontalAlignment.Left);
        _tileSizeFmt = Fmt(11, Microsoft.UI.Text.FontWeights.Normal, CanvasHorizontalAlignment.Left);
        _centerFmt = Fmt(12, Microsoft.UI.Text.FontWeights.Normal, CanvasHorizontalAlignment.Center);
        _centerFmt.WordWrapping = CanvasWordWrapping.Wrap;
        _badgeFmt = Fmt(10, Microsoft.UI.Text.FontWeights.Bold, CanvasHorizontalAlignment.Center);
        _cache?.Dispose();
        _cache = null;
        _cacheValid = false;
    }

    private static CanvasTextFormat Fmt(float size, Windows.UI.Text.FontWeight weight, CanvasHorizontalAlignment align) => new()
    {
        FontFamily = "Segoe UI Variable Text",
        FontSize = size,
        FontWeight = weight,
        HorizontalAlignment = align,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap,
        TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        TrimmingSign = CanvasTrimmingSign.Ellipsis,
    };

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
            DrawSelection(ds, _boxes, null);
        }
        _cacheValid = true;
    }

    private CanvasRenderTarget? _morphEnd;

    private void PrepareMorphEnd()
    {
        if (ActualWidth < 4 || _cushion == null)
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
        if (_cushion == null) return;
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
        ds.FillRoundedRectangle(new Rect(0, 0, size.Width, size.Height), 8, 8, _pal.Background);
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

    private void DrawBox(CanvasDrawingSession ds, MapBox box, RectD r, float alpha, bool labels)
    {
        if (IsDimmed(box)) alpha *= 0.2f;
        var rr = new Rect(r.X + 0.5, r.Y + 0.5, Math.Max(0, r.W - 1), Math.Max(0, r.H - 1));
        switch (box.Kind)
        {
            case BoxKind.Frame:
            {
                var node = box.Node!;
                var c = CategoryColor(node.Category);
                ds.FillRoundedRectangle(rr, 4, 4, WithAlpha(c, 0.10f * alpha));
                ds.DrawRoundedRectangle(rr, 4, 4, WithAlpha(_pal.FrameStroke, alpha), 1f, node.IsProtected ? _dash : null);
                var head = new Rect(rr.X + 1, rr.Y + 1, Math.Max(0, rr.Width - 2), Math.Min(16, Math.Max(0, rr.Height - 2)));
                ds.FillRoundedRectangle(head, 3, 3, WithAlpha(c, 0.36f * alpha));
                if (labels && head.Width >= 28)
                {
                    double sizeW = head.Width >= 110 ? 64 : 0;
                    double badgeW = 0;
                    if (Growth != null && head.Width >= 170 && Growth.TryGetValue(node, out var delta))
                    {
                        badgeW = DrawBadge(ds, head.Right - sizeW - 6, head.Y + 1, delta, alpha, rightAligned: true) + 6;
                    }
                    ds.DrawText(node.DisplayName, new Rect(head.X + 5, head.Y, Math.Max(0, head.Width - 10 - sizeW - badgeW), head.Height),
                        WithAlpha(_pal.Text, alpha), _headFmt);
                    if (sizeW > 0)
                    {
                        ds.DrawText(Format.Bytes(node.Size), new Rect(head.X + 5, head.Y, head.Width - 11, head.Height),
                            WithAlpha(_pal.TextSecondary, alpha), _headSizeFmt);
                    }
                }
                break;
            }
            case BoxKind.Tile:
            {
                var node = box.Node!;
                var c = CategoryColor(node.Category);
                float radius = (float)Math.Min(3, Math.Min(rr.Width, rr.Height) / 4);
                ds.FillRoundedRectangle(rr, radius, radius, WithAlpha(c, alpha));
                if (rr.Width * rr.Height >= 64)
                {
                    _cushion!.StartPoint = new Vector2((float)rr.X, (float)rr.Y);
                    _cushion.EndPoint = new Vector2((float)(rr.X + rr.Width * 0.9), (float)(rr.Y + rr.Height));
                    _cushion.Opacity = alpha;
                    ds.FillRoundedRectangle(rr, radius, radius, _cushion);
                }
                if (node.IsProtected && rr.Width * rr.Height >= 64)
                {
                    _hatch!.Opacity = alpha;
                    ds.FillRoundedRectangle(rr, radius, radius, _hatch);
                }
                if (labels && rr.Width >= 40 && rr.Height >= 18)
                {
                    var ink = WithAlpha(Categories.NeedsLightText(node.Category) ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 20, 20, 20), alpha);
                    ds.DrawText(node.DisplayName, new Rect(rr.X + 5, rr.Y + 2, rr.Width - 10, 14), ink, _tileFmt);
                    if (rr.Height >= 32)
                    {
                        ds.DrawText(Format.Bytes(node.Size), new Rect(rr.X + 5, rr.Y + 16, rr.Width - 10, 14), WithAlpha(ink, 0.82f * alpha), _tileSizeFmt);
                    }
                    if (Growth != null && rr.Width >= 64 && rr.Height >= 52 && Growth.TryGetValue(node, out var delta))
                    {
                        DrawBadge(ds, rr.X + 4, rr.Bottom - 18, delta, alpha, rightAligned: false);
                    }
                }
                break;
            }
            case BoxKind.Free:
            {
                ds.DrawRoundedRectangle(rr, 4, 4, WithAlpha(_pal.TextTertiary, alpha), 1f, _dash);
                if (labels && rr.Width >= 60 && rr.Height >= 34)
                {
                    ds.DrawText($"Free space\n{Format.Bytes(box.Size)}", rr, WithAlpha(_pal.TextSecondary, alpha), _centerFmt);
                }
                break;
            }
            case BoxKind.Rest:
            {
                float radius = (float)Math.Min(3, Math.Min(rr.Width, rr.Height) / 4);
                ds.FillRoundedRectangle(rr, radius, radius, WithAlpha(_pal.Rest, alpha));
                if (labels && rr.Width >= 70 && rr.Height >= 30)
                {
                    ds.DrawText($"{box.RestCount:N0} small items\n{Format.Bytes(box.Size)}", rr, WithAlpha(_pal.TextSecondary, alpha), _centerFmt);
                }
                break;
            }
        }
    }

    private float DrawBadge(CanvasDrawingSession ds, double x, double y, long delta, float alpha, bool rightAligned)
    {
        var text = Format.Delta(delta);
        float w = 10 + text.Length * 5.6f;
        var r = new Rect(rightAligned ? x - w : x, y, w, 14);
        ds.FillRoundedRectangle(r, 7, 7, WithAlpha(delta >= 0 ? _pal.Caution : _pal.Success, alpha));
        ds.DrawText(text, r, WithAlpha(_pal.OnSeverity, alpha), _badgeFmt);
        return w;
    }

    private void DrawSelection(CanvasDrawingSession ds, List<MapBox> boxes, Func<MapBox, RectD>? rects)
    {
        foreach (var box in boxes)
        {
            if (box.Node == null) continue;
            bool selected = Selection?.Contains(box.Node) == true;
            bool focused = ReferenceEquals(FocusNode, box.Node);
            if (!selected && !focused) continue;
            var r = rects?.Invoke(box) ?? box.Rect;
            var rr = new Rect(r.X + 1.5, r.Y + 1.5, Math.Max(0, r.W - 3), Math.Max(0, r.H - 3));
            ds.DrawRoundedRectangle(rr, 3, 3, selected ? _pal.Accent : _pal.Text, 2f);
            if (selected && rr.Width >= 24 && rr.Height >= 24)
            {
                var c = new Vector2((float)(rr.Right - 10), (float)(rr.Y + (box.Kind == BoxKind.Frame ? 8 : 10)));
                ds.FillCircle(c, 7, _pal.Accent);
                ds.DrawLine(c + new Vector2(-3.2f, 0.2f), c + new Vector2(-0.8f, 2.6f), _pal.OnAccent, 1.6f);
                ds.DrawLine(c + new Vector2(-0.8f, 2.6f), c + new Vector2(3.4f, -2.4f), _pal.OnAccent, 1.6f);
            }
        }
    }

    private void DrawHover(CanvasDrawingSession ds)
    {
        if (_hover == null) return;
        for (var p = _hover.Parent; p != null; p = p.Parent)
        {
            var pr = p.Rect;
            ds.DrawRoundedRectangle(new Rect(pr.X + 0.5, pr.Y + 0.5, pr.W - 1, pr.H - 1), 4, 4, WithAlpha(_pal.TextSecondary, 0.55f), 1f);
        }
        var r = _hover.Rect;
        ds.DrawRoundedRectangle(new Rect(r.X + 0.5, r.Y + 0.5, Math.Max(0, r.W - 1), Math.Max(0, r.H - 1)), 3, 3, _pal.Text, 1.5f);
    }

    private static Color CategoryColor(Category c)
    {
        uint rgb = Categories.Rgb(c);
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
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

    private readonly record struct Palette(Color Background, Color FrameStroke, Color Text, Color TextSecondary, Color TextTertiary,
        Color Rest, Color Accent, Color OnAccent, Color Caution, Color Success, Color OnSeverity)
    {
        private static Color Hex(uint rgb, byte a = 255) => Color.FromArgb(a, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static Color SystemAccent(UIColorType type, uint fallback)
        {
            try { return new UISettings().GetColorValue(type); }
            catch (Exception) { return Hex(fallback); }
        }

        public static Palette Dark() => new(Hex(0x1A1A1A), Hex(0xFFFFFF, 26), Hex(0xFFFFFF), Hex(0xC5C5C5), Hex(0x8F8F8F),
            Hex(0xFFFFFF, 30), SystemAccent(UIColorType.AccentLight2, 0x60CDFF), Hex(0x000000), Hex(0xFCE100), Hex(0x6CCB5F), Hex(0x000000));

        public static Palette Light() => new(Hex(0xEBEBEB), Hex(0x000000, 34), Hex(0x1A1A1A), Hex(0x5D5D5D), Hex(0x8A8A8A),
            Hex(0x000000, 26), SystemAccent(UIColorType.AccentDark1, 0x005FB8), Hex(0xFFFFFF), Hex(0x9D5D00), Hex(0x0F7B0F), Hex(0xFFFFFF));
    }
}

internal static class RectExtensions
{
    public static Size Size(this Rect r) => new(r.Width, r.Height);
}
