using System.Numerics;
using Cascade.UI;

namespace Peepr;

/// <summary>
/// The surface pictures and videos are drawn on. Content opens fitted to the window (a photo is not enlarged
/// past its own pixels unless <see cref="EnlargeToFit"/>; a video fills the window as players do), except that a photo following a zoomed photo
/// opens zoomed onto the same spot; the wheel zooms around the cursor, dragging
/// pans while zoomed, double-click flips between fit and actual size, and animations play. It owns the textures
/// of the photos it shows and frees them when the content changes; a video's texture belongs to its
/// <see cref="VideoPlayback"/>.
/// </summary>
internal sealed class PictureStage
{
    private const float MaxZoomOverActualSize = 32f;
    // Zoom per wheel notch. Cascade reports one notch as DeltaY = ±1 (positive = away from you = zoom in);
    // trackpads report fractions, which this scales smoothly.
    private const float WheelZoomStep = 1.18f;
    // How often (in video time) the video controls are re-rendered to move the seek bar along.
    private const long ProgressStepTicks = TimeSpan.TicksPerSecond / 4;

    // The photo, or the animation frame on show (the player writes each frame into it in place).
    private ImageSource? picture;
    private AnimationPlayer? animation;
    // Halved copies of a large still, largest first (see DecodedPicture.SmallerLevels).
    private ImageSource[] smallerLevels = [];
    private int pictureWidth;
    private int pictureHeight;

    private VideoPlayback? video;
    private long lastReportedProgress = -1;

    // Logical units per content pixel while zoomed; null while fitted to the window.
    private float? zoom;
    private Vector2 pan;
    private int quarterTurns;
    // The zoom a photo was left at, carried to the next one so a spot can be compared across several images:
    // magnification relative to the window fit, and pan as a fraction of the drawn size. Survives Clear(), which
    // runs while the next file decodes.
    private (float OverFit, Vector2 PanFraction)? carriedView;

    private Size viewport;
    private float pixelRatio = 1f;
    private Point pointer;

    /// <summary>True when something is on stage to draw: a photo, or a video that has shown its first frame.</summary>
    public bool HasPicture => picture is not null || video?.Picture is not null;

    public bool IsZoomed => zoom is not null;

    /// <summary>Fit enlarges a photo smaller than the window to fill it, as it does a video.</summary>
    public bool EnlargeToFit { get; set; }

    private int ContentWidth => video?.VideoWidth ?? pictureWidth;

    private int ContentHeight => video?.VideoHeight ?? pictureHeight;

    /// <summary>Replaces what's on stage with a photo, or an animation that starts playing.</summary>
    public void Show(DecodedPicture decoded)
    {
        // A still shares the decoded buffer with the renderer rather than copying it. An animation's frames are
        // written into its image, so it gets a copy: the cached first frame must stay the first frame.
        byte[] pixels = decoded.IsAnimated ? (byte[])decoded.Pixels.Clone() : decoded.Pixels;
        ImageSource uploaded = ImageSource.FromPixels(pixels, decoded.Width, decoded.Height);

        var levels = new ImageSource[decoded.SmallerLevels.Length];
        for (int i = 0; i < levels.Length; i++)
        {
            PictureLevel level = decoded.SmallerLevels[i];
            levels[i] = ImageSource.FromPixels(level.Pixels, level.Width, level.Height);
        }

        Release();
        picture = uploaded;
        animation = decoded.Animation is { } source ? new AnimationPlayer(source, uploaded) : null;
        smallerLevels = levels;
        pictureWidth = decoded.Width;
        pictureHeight = decoded.Height;
        quarterTurns = 0;
        ResetZoom();
        ApplyCarriedView();
    }

    /// <summary>Replaces what's on stage with a playing video. The stage draws it but does not own it.</summary>
    public void ShowVideo(VideoPlayback playback)
    {
        Release();
        video = playback;
        lastReportedProgress = -1;
        quarterTurns = 0;
        ResetZoom();
    }

    public void Clear() => Release();

    public void Rotate()
    {
        quarterTurns = (quarterTurns + 1) & 3;
        ResetZoom();
    }

    public void ResetZoom()
    {
        zoom = null;
        pan = Vector2.Zero;
    }

    /// <summary>Zooms in or out around the centre of the window (keyboard + / −).</summary>
    public void ZoomBy(float factor) =>
        ZoomAround(factor, new Point(viewport.Width / 2f, viewport.Height / 2f));

    /// <summary>Fit ↔ actual size (1 content pixel per screen pixel), centred on <paramref name="at"/>.</summary>
    public void ToggleActualSize(Point at)
    {
        if (zoom is not null)
        {
            ResetZoom();
            return;
        }

        float fit = FitScale();
        float actual = 1f / pixelRatio;
        // Content already shown at (or past) actual size has nothing to zoom to; go to double instead.
        float target = actual > fit * 1.01f ? actual : fit * 2f;
        ZoomAround(target / fit, at);
    }

    /// <summary>
    /// The stage as a node. <paramref name="redraw"/> re-renders after a view change; <paramref name="onActivity"/>
    /// is told about pointer movement so the window chrome can show itself; <paramref name="onVideoProgress"/> is
    /// told when a video shows its first frame, moves on a quarter second, or stops.
    /// </summary>
    public Node Build(Action redraw, Action onActivity, Action onVideoProgress, Action onMiddleClick)
    {
        // Only ask for frames while something moves: a still photo, a paused or finished video draw once.
        Action<float>? onFrame = video is { NeedsFrames: true }
            ? _ => PumpVideo(onVideoProgress)
            : animation is not null ? AdvanceAnimation : null;

        return CanvasFactory.Canvas(Size.Fill, Draw, onFrame)
            .OnScroll(delta =>
            {
                if (HasPicture && delta.Y != 0)
                {
                    ZoomAround(MathF.Pow(WheelZoomStep, delta.Y), pointer);
                    redraw();
                }
            })
            .OnPointerMove(e =>
            {
                pointer = e.Position;
                onActivity();
            })
            .OnPointerUp(e =>
            {
                if (e.Button == NativeMouseButton.Middle)
                {
                    onMiddleClick();
                }
            })
            .OnPan(delta =>
            {
                // A drag while zoomed moves the content with the pointer.
                if (zoom is not null)
                {
                    pan += new Vector2(delta.X, delta.Y);
                    redraw();
                }
            })
            .OnDoubleTap(() =>
            {
                if (HasPicture)
                {
                    ToggleActualSize(pointer);
                    redraw();
                }
            });
    }

    private void PumpVideo(Action onVideoProgress)
    {
        if (video is null)
        {
            return;
        }

        bool firstPicture = video.PumpFrame();
        long progress = video.Position.Ticks / ProgressStepTicks;
        if (firstPicture || progress != lastReportedProgress || !video.NeedsFrames)
        {
            lastReportedProgress = progress;
            onVideoProgress();
        }
    }

    private void AdvanceAnimation(float deltaSeconds) => animation?.Advance(deltaSeconds * 1000f);

    private void Draw(DrawContext context, Size size)
    {
        viewport = size;
        pixelRatio = context.PixelRatio > 0 ? context.PixelRatio : 1f;
        ImageSource? texture = video is not null ? video.Picture : picture;
        if (texture is null || ContentWidth == 0 || ContentHeight == 0)
        {
            return;
        }

        float scale = zoom ?? FitScale();
        float width = ContentWidth * scale;
        float height = ContentHeight * scale;
        bool sideways = (quarterTurns & 1) == 1;
        pan = ClampPan(pan, sideways ? height : width, sideways ? width : height);

        // The player scales frames down to what is drawn; zooming in asks it for more pixels.
        video?.SetDisplaySize((int)MathF.Ceiling(width * pixelRatio), (int)MathF.Ceiling(height * pixelRatio));

        // A large still drawn small uses the smallest halved copy that still has a texel per device pixel, so the
        // GPU never minifies more than 2:1 and the picture stays smooth instead of shimmering. Zooming in climbs
        // back to the full-size texture, which is only uploaded once it is needed.
        float deviceWidth = width * pixelRatio;
        foreach (ImageSource level in smallerLevels)
        {
            if (level.Width < deviceWidth)
            {
                break;
            }
            texture = level;
        }

        // Snap the corner to a device pixel so actual size is pixel-exact rather than smeared.
        float left = Snap((size.Width - width) / 2f + pan.X);
        float top = Snap((size.Height - height) / 2f + pan.Y);

        if (quarterTurns == 0)
        {
            context.DrawImage(texture, new Rect(left, top, width, height));
            return;
        }

        float centerX = left + width / 2f;
        float centerY = top + height / 2f;
        using var turn = context.PushRotate(Angle.Degrees(90f * quarterTurns), new Point(centerX, centerY));
        context.DrawImage(texture, new Rect(left, top, width, height));
    }

    private float Snap(float value) => MathF.Round(value * pixelRatio) / pixelRatio;

    // The scale that fits the (possibly rotated) content in the window. Photos are not enlarged past their own
    // pixels unless EnlargeToFit is on; video fills the window.
    private float FitScale()
    {
        int contentWidth = ContentWidth;
        int contentHeight = ContentHeight;
        if (contentWidth == 0 || contentHeight == 0 || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return 1f;
        }

        bool sideways = (quarterTurns & 1) == 1;
        float width = sideways ? contentHeight : contentWidth;
        float height = sideways ? contentWidth : contentHeight;
        float fit = MathF.Min(viewport.Width / width, viewport.Height / height);
        return video is not null || EnlargeToFit ? fit : MathF.Min(fit, 1f / pixelRatio);
    }

    private void ZoomAround(float factor, Point at)
    {
        if (!HasPicture)
        {
            return;
        }

        float fit = FitScale();
        float current = zoom ?? fit;
        float next = Math.Clamp(current * factor, fit, MathF.Max(fit, MaxZoomOverActualSize / pixelRatio));
        if (next <= fit * 1.0001f)
        {
            // Zoomed back out to the window: return to fit, which follows window resizes.
            ResetZoom();
            return;
        }

        // Keep the content point under the cursor where it is.
        var fromCenter = new Vector2(at.X - viewport.Width / 2f, at.Y - viewport.Height / 2f) - pan;
        pan += fromCenter - fromCenter * (next / current);
        zoom = next;
    }

    // Content larger than the window can pan until its edge meets the window's; smaller content stays centred.
    private Vector2 ClampPan(Vector2 offset, float drawnWidth, float drawnHeight)
    {
        float slackX = MathF.Max(0f, (drawnWidth - viewport.Width) / 2f);
        float slackY = MathF.Max(0f, (drawnHeight - viewport.Height) / 2f);
        return new Vector2(Math.Clamp(offset.X, -slackX, slackX), Math.Clamp(offset.Y, -slackY, slackY));
    }

    private void ApplyCarriedView()
    {
        if (carriedView is not { } view)
        {
            return;
        }

        float fit = FitScale();
        float next = MathF.Min(fit * view.OverFit, MathF.Max(fit, MaxZoomOverActualSize / pixelRatio));
        if (next > fit * 1.0001f)
        {
            zoom = next;
            pan = view.PanFraction * new Vector2(ContentWidth * next, ContentHeight * next);
        }
    }

    // Remembers the zoom of a photo that is leaving the stage (see carriedView); an unzoomed, rotated or video
    // view drops it. Nothing is on stage after a Clear(), so a second release keeps what the first one saved.
    private void CarryView()
    {
        if (ContentWidth == 0 || ContentHeight == 0)
        {
            return;
        }

        carriedView = zoom is { } scale && video is null && quarterTurns == 0
            ? (scale / FitScale(), pan / new Vector2(ContentWidth * scale, ContentHeight * scale))
            : null;
    }

    private void Release()
    {
        CarryView();
        // The player first, so it never writes into a disposed image.
        animation?.Dispose();
        animation = null;
        picture?.Dispose();
        picture = null;
        foreach (ImageSource level in smallerLevels)
        {
            level.Dispose();
        }
        smallerLevels = [];
        pictureWidth = 0;
        pictureHeight = 0;
        video = null;
    }
}
