using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;
using Source2MapCompiler.LightmapPreview;

namespace Source2MapCompiler;

// Drag to pan, scroll to zoom, double click to fit it back in view. The lightmap is drawn on the render thread by
// LightmapVisual, which keeps it on the GPU and exposes it there, so the view only tells it what to show
[SupportedOSPlatform("windows")]
public sealed class LightmapView : Control
{
    public static readonly StyledProperty<bool> ShowBlocksProperty = AvaloniaProperty.Register<LightmapView, bool>(nameof(ShowBlocks), true);

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(16, 16, 16));

    private LightmapAtlas? atlas;
    private CompositionCustomVisual? visual;
    private float exposure;
    private double zoom = 1;
    private Point offset;
    private Point? dragStart;
    private Point dragOffset;
    private bool fitPending;

    // Keeps the atlas fitted while the view resizes, until it's dragged or zoomed
    private bool autoFit = true;

    public LightmapView()
    {
        ClipToBounds = true;
    }

    public bool ShowBlocks
    {
        get => GetValue(ShowBlocksProperty);
        set => SetValue(ShowBlocksProperty, value);
    }

    // in stops up or down from the auto exposure
    public float Exposure
    {
        get => exposure;
        set
        {
            exposure = value;
            SendFrame();
        }
    }

    public void SetAtlas(LightmapAtlas value)
    {
        atlas = value;
        autoFit = true;
        fitPending = true;
        SendFrame();
        InvalidateArrange();
    }

    // The atlas's texels changed, which the visual picks up from its version
    public void Refresh()
    {
        SendFrame();
    }

    public void FitToView()
    {
        if (atlas == null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        zoom = Math.Min(Bounds.Width / atlas.Width, Bounds.Height / atlas.Height) * 0.95;
        offset = new Point((Bounds.Width - atlas.Width * zoom) / 2, (Bounds.Height - atlas.Height * zoom) / 2);
        autoFit = true;
        fitPending = false;
        SendFrame();
    }

    // The lightmap as it's shown, at its full size, as a PNG. It's drawn on the CPU so it doesn't depend on the GPU
    public async Task SavePng(Stream stream)
    {
        if (atlas is not { } saved)
        {
            return;
        }

        var stops = exposure;
        var png = await Task.Run(() =>
        {
            using var image = LightmapVisual.Upload(saved, null)!;
            using var surface = SKSurface.Create(new SKImageInfo(saved.Width, saved.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var paint = new SKPaint { Shader = LightmapShader.Create(image, Tonemap.Exposure(saved.AverageLuminance, stops), nearest: true) };
            surface.Canvas.DrawRect(0, 0, saved.Width, saved.Height, paint);
            using var snapshot = surface.Snapshot();
            return snapshot.Encode(SKEncodedImageFormat.Png, 100);
        });

        using (png)
        {
            png.SaveTo(stream);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ShowBlocksProperty || change.Property == BoundsProperty)
        {
            SendFrame();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (ElementComposition.GetElementVisual(this)?.Compositor is { } compositor)
        {
            visual = compositor.CreateCustomVisual(new LightmapVisual());
            visual.ClipToBounds = true;
            ElementComposition.SetElementChildVisual(this, visual);
            SendFrame();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        // the GPU texture can only be let go of on the render thread
        visual?.SendHandlerMessage(LightmapVisual.Release);
        ElementComposition.SetElementChildVisual(this, null);
        visual = null;
        atlas = null;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);

        if (fitPending || (autoFit && size != Bounds.Size))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(FitToView);
        }

        return size;
    }

    // Filled so the whole view picks up pointer input, the lightmap is drawn over it by the visual
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var at = e.GetPosition(this);
        var newZoom = Math.Clamp(zoom * (e.Delta.Y > 0 ? 1.25 : 0.8), 0.02, 64);
        offset = new Point(at.X - (at.X - offset.X) * newZoom / zoom, at.Y - (at.Y - offset.Y) * newZoom / zoom);
        zoom = newZoom;
        autoFit = false;
        fitPending = false;
        SendFrame();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.ClickCount == 2)
        {
            FitToView();
            return;
        }

        dragStart = e.GetPosition(this);
        dragOffset = offset;
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (dragStart is { } start)
        {
            var at = e.GetPosition(this);
            offset = new Point(dragOffset.X + at.X - start.X, dragOffset.Y + at.Y - start.Y);
            autoFit = false;
            fitPending = false;
            SendFrame();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        dragStart = null;
        e.Pointer.Capture(null);
        Cursor = null;
    }

    private void SendFrame()
    {
        if (visual == null)
        {
            return;
        }

        visual.Size = new Vector(Bounds.Width, Bounds.Height);
        visual.SendHandlerMessage(new LightmapVisual.Frame(atlas, exposure, zoom, offset, ShowBlocks));
    }
}

// Draws the lightmap on the render thread, which is the only thread that may touch the GPU texture it's kept in. The
// texture is made again whenever the atlas's texels change, at most twice a second, since an 8K lightmap is half a gigabyte
// to upload and the bake changes it in blocks
[SupportedOSPlatform("windows")]
internal sealed class LightmapVisual : CompositionCustomVisualHandler
{
    public sealed record Frame(LightmapAtlas? Atlas, float Exposure, double Zoom, Point Offset, bool ShowBlocks);

    public static readonly object Release = new();

    private static readonly TimeSpan UploadInterval = TimeSpan.FromMilliseconds(500);

    private static readonly SKPaint BlockPaint = new() { Color = new SKColor(255, 255, 255, 77), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private static readonly SKPaint BakingPaint = new() { Color = SKColors.Gold, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
    private static readonly SKPaint PendingPaint = new() { Color = new SKColor(0, 0, 0, 89) };

    private Frame? frame;
    private SKImage? image;
    private LightmapAtlas? uploaded;
    private int uploadedVersion;
    private DateTime uploadedAt;

    // The atlas's texels as an image, on the GPU when there is one
    public static SKImage? Upload(LightmapAtlas atlas, GRContext? gpu)
    {
        SKImage? result = null;

        atlas.ReadPixels((pixels, rowBytes) =>
        {
            var info = new SKImageInfo(atlas.Width, atlas.Height, SKColorType.RgbaF16, SKAlphaType.Premul);
            using var wrapped = SKImage.FromPixels(info, pixels, rowBytes);
            result = gpu != null ? wrapped.ToTextureImage(gpu, true) : null;
            result ??= SKImage.FromPixelCopy(info, pixels, rowBytes);
        });

        return result;
    }

    public override void OnMessage(object message)
    {
        if (message == Release)
        {
            image?.Dispose();
            image = null;
            uploaded = null;
            frame = null;
            return;
        }

        frame = (Frame)message;
        Invalidate();
    }

    public override void OnAnimationFrameUpdate()
    {
        Invalidate();
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (frame is not { Atlas: { } atlas } current || context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } skia)
        {
            return;
        }

        using var lease = skia.Lease();
        var canvas = lease.SkCanvas;

        if (atlas != uploaded || atlas.Version != uploadedVersion)
        {
            if (atlas == uploaded && DateTime.UtcNow - uploadedAt < UploadInterval)
            {
                RegisterForNextAnimationFrameUpdate();
            }
            else
            {
                var version = atlas.Version;
                image?.Dispose();
                image = Upload(atlas, lease.GrContext);
                uploaded = atlas;
                uploadedVersion = version;
                uploadedAt = DateTime.UtcNow;
            }
        }

        if (image == null)
        {
            return;
        }

        var zoom = (float)current.Zoom;
        var x = (float)current.Offset.X;
        var y = (float)current.Offset.Y;

        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(zoom);

        using (var paint = new SKPaint { Shader = LightmapShader.Create(image, Tonemap.Exposure(atlas.AverageLuminance, current.Exposure), nearest: zoom >= 1) })
        {
            canvas.DrawRect(0, 0, atlas.Width, atlas.Height, paint);
        }

        canvas.Restore();

        var shown = SKRect.Create(x, y, atlas.Width * zoom, atlas.Height * zoom);
        var block = atlas.BlockSize * zoom;

        for (var by = 0; by < atlas.BlocksY; by++)
        {
            for (var bx = 0; bx < atlas.BlocksX; bx++)
            {
                var rect = SKRect.Intersect(SKRect.Create(x + bx * block, y + by * block, block, block), shown);
                var state = atlas.GetBlockState(bx, by);

                // The block being baked gets outlined even when the grid is hidden
                if (state == LightmapBlockState.Baking)
                {
                    canvas.DrawRect(rect, BakingPaint);
                }
                else if (current.ShowBlocks)
                {
                    if (state == LightmapBlockState.Pending)
                    {
                        canvas.DrawRect(rect, PendingPaint);
                    }

                    canvas.DrawRect(rect, BlockPaint);
                }
            }
        }
    }
}
