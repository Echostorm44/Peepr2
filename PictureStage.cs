using System.Numerics;
using Cascade.UI;

namespace Peepr;

/// <summary>
/// The surface pictures are drawn on. A picture opens fitted to the window (never enlarged past its own pixels);
/// the wheel zooms around the cursor, dragging pans a zoomed picture, double-click flips between fit and actual
/// size, and animations play. It owns its GPU textures and frees them when the picture changes.
/// </summary>
internal sealed class PictureStage
{
    private const float MaxZoomOverActualSize = 32f;
    private const float WheelZoomStep = 1.0015f;

    private ImageSource[] frames = [];
    private int[] frameDurationsMs = [];
    private int frameIndex;
    private float frameElapsedMs;
    private int pictureWidth;
    private int pictureHeight;

    // Logical units per picture pixel while zoomed; null while fitted to the window.
    private float? zoom;
    private Vector2 pan;
    private int quarterTurns;

    private Size viewport;
    private float pixelRatio = 1f;
    private Point pointer;
    private bool dragging;

    public bool HasPicture => frames.Length > 0;

    public bool IsZoomed => zoom is not null;

    public int PictureWidth => pictureWidth;

    public int PictureHeight => pictureHeight;

    /// <summary>Replaces what's on stage, uploading every frame of <paramref name="picture"/>.</summary>
    public void Show(DecodedPicture picture)
    {
        var uploaded = new ImageSource[picture.Frames.Length];
        for (int i = 0; i < uploaded.Length; i++)
        {
            uploaded[i] = ImageSource.FromBytes(picture.Frames[i], picture.Width, picture.Height);
        }

        Release();
        frames = uploaded;
        frameDurationsMs = picture.FrameDurationsMs;
        pictureWidth = picture.Width;
        pictureHeight = picture.Height;
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

    /// <summary>Fit ↔ actual size (1 picture pixel per screen pixel), centred on <paramref name="at"/>.</summary>
    public void ToggleActualSize(Point at)
    {
        if (zoom is not null)
        {
            ResetZoom();
            return;
        }

        float fit = FitScale();
        float actual = 1f / pixelRatio;
        // A picture already shown at actual size has nothing to zoom to; go to double instead.
        float target = actual > fit * 1.01f ? actual : actual * 2f;
        ZoomAround(target / fit, at);
    }

    /// <summary>
    /// The stage as a node. <paramref name="redraw"/> re-renders after a view change; <paramref name="onActivity"/>
    /// is told about pointer movement so the window chrome can show itself.
    /// </summary>
    public Node Build(Action redraw, Action onActivity)
    {
        Action<float>? onFrame = frames.Length > 1 ? AdvanceAnimation : null;
        return CanvasFactory.Canvas(Size.Fill, Draw, onFrame)
            .OnScroll(delta =>
            {
                if (HasPicture && delta.Y != 0)
                {
                    ZoomAround(MathF.Pow(WheelZoomStep, -delta.Y), pointer);
                    redraw();
                }
            })
            .OnPointerMove(e =>
            {
                pointer = e.Position;
                if (dragging && zoom is not null)
                {
                    pan += new Vector2(e.Delta.X, e.Delta.Y);
                    redraw();
                }
                onActivity();
            })
            .OnPointerDown(e =>
            {
                pointer = e.Position;
                dragging = true;
            })
            .OnPointerUp(_ => dragging = false)
            .OnDoubleTap(() =>
            {
                if (HasPicture)
                {
                    ToggleActualSize(pointer);
                    redraw();
                }
            });
    }

    private void AdvanceAnimation(float deltaSeconds)
    {
        if (frames.Length < 2)
        {
            return;
        }

        frameElapsedMs += deltaSeconds * 1000f;
        while (frameElapsedMs >= frameDurationsMs[frameIndex])
        {
            frameElapsedMs -= frameDurationsMs[frameIndex];
            frameIndex = (frameIndex + 1) % frames.Length;
        }
    }

    private void Draw(DrawContext context, Size size)
    {
        viewport = size;
        pixelRatio = context.PixelRatio > 0 ? context.PixelRatio : 1f;
        if (frames.Length == 0)
        {
            return;
        }

        float scale = zoom ?? FitScale();
        float width = pictureWidth * scale;
        float height = pictureHeight * scale;
        bool sideways = (quarterTurns & 1) == 1;
        pan = ClampPan(pan, sideways ? height : width, sideways ? width : height);

        // Snap the picture's corner to a device pixel so actual size is pixel-exact rather than smeared.
        float left = Snap((size.Width - width) / 2f + pan.X);
        float top = Snap((size.Height - height) / 2f + pan.Y);
        ImageSource frame = frames[frameIndex];

        if (quarterTurns == 0)
        {
            context.DrawImage(frame, new Rect(left, top, width, height));
            return;
        }

        float centerX = left + width / 2f;
        float centerY = top + height / 2f;
        using var turn = context.PushRotate(Angle.Degrees(90f * quarterTurns), new Point(centerX, centerY));
        context.DrawImage(frame, new Rect(left, top, width, height));
    }

    private float Snap(float value) => MathF.Round(value * pixelRatio) / pixelRatio;

    // The scale that fits the (possibly rotated) picture in the window, never enlarging past actual pixels.
    private float FitScale()
    {
        if (pictureWidth == 0 || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return 1f;
        }

        bool sideways = (quarterTurns & 1) == 1;
        float width = sideways ? pictureHeight : pictureWidth;
        float height = sideways ? pictureWidth : pictureHeight;
        float fit = MathF.Min(viewport.Width / width, viewport.Height / height);
        return MathF.Min(fit, 1f / pixelRatio);
    }

    private void ZoomAround(float factor, Point at)
    {
        if (!HasPicture)
        {
            return;
        }

        float fit = FitScale();
        float current = zoom ?? fit;
        float next = Math.Clamp(current * factor, fit, MaxZoomOverActualSize / pixelRatio);
        if (next <= fit * 1.0001f)
        {
            // Zoomed back out to the window: return to fit, which follows window resizes.
            ResetZoom();
            return;
        }

        // Keep the picture point under the cursor where it is.
        var fromCenter = new Vector2(at.X - viewport.Width / 2f, at.Y - viewport.Height / 2f) - pan;
        pan += fromCenter - fromCenter * (next / current);
        zoom = next;
    }

    // A picture larger than the window can pan until its edge meets the window's; a smaller one stays centred.
    private Vector2 ClampPan(Vector2 offset, float drawnWidth, float drawnHeight)
    {
        float slackX = MathF.Max(0f, (drawnWidth - viewport.Width) / 2f);
        float slackY = MathF.Max(0f, (drawnHeight - viewport.Height) / 2f);
        return new Vector2(Math.Clamp(offset.X, -slackX, slackX), Math.Clamp(offset.Y, -slackY, slackY));
    }

    private void Release()
    {
        foreach (ImageSource frame in frames)
        {
            frame.Dispose();
        }
        frames = [];
        frameDurationsMs = [];
        frameIndex = 0;
        frameElapsedMs = 0;
        pictureWidth = 0;
        pictureHeight = 0;
    }
}
