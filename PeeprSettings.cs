using System.Text.Json.Serialization;
using Cascade.UI;

namespace Peepr;

/// <summary>
/// The user's preferences, persisted through Cascade's <see cref="LocalStorage"/> as plain values with
/// source-generated JSON (no reflection, so they survive Native AOT).
/// </summary>
internal static class PeeprSettings
{
    public const int MinimumSlideshowSeconds = 1;
    public const int MaximumSlideshowSeconds = 120;

    private static readonly SettingsJsonContext Json = SettingsJsonContext.Default;

    public static int SlideshowSeconds
    {
        get => Math.Clamp(LocalStorage.Get("slideshowSeconds", 5, Json.Int32), MinimumSlideshowSeconds, MaximumSlideshowSeconds);
        set => LocalStorage.Set("slideshowSeconds", Math.Clamp(value, MinimumSlideshowSeconds, MaximumSlideshowSeconds), Json.Int32);
    }

    public static SortOrder SortOrder
    {
        get => (SortOrder)Math.Clamp(LocalStorage.Get("sortOrder", 0, Json.Int32), 0, (int)SortOrder.Type);
        set => LocalStorage.Set("sortOrder", (int)value, Json.Int32);
    }

    public static bool ConfirmBeforeDelete
    {
        get => LocalStorage.Get("confirmBeforeDelete", false, Json.Boolean);
        set => LocalStorage.Set("confirmBeforeDelete", value, Json.Boolean);
    }

    /// <summary>Enlarge photos smaller than the window to fill it, rather than showing them at their own size.</summary>
    public static bool FitToWindow
    {
        get => LocalStorage.Get("fitToWindow", false, Json.Boolean);
        set => LocalStorage.Set("fitToWindow", value, Json.Boolean);
    }

    public static bool ShowThumbnailStrip
    {
        get => LocalStorage.Get("showThumbnailStrip", false, Json.Boolean);
        set => LocalStorage.Set("showThumbnailStrip", value, Json.Boolean);
    }

    public static float Volume
    {
        get => Math.Clamp(LocalStorage.Get("volume", 0.8f, Json.Single), 0f, 1f);
        set => LocalStorage.Set("volume", Math.Clamp(value, 0f, 1f), Json.Single);
    }

    public static bool Muted
    {
        get => LocalStorage.Get("muted", false, Json.Boolean);
        set => LocalStorage.Set("muted", value, Json.Boolean);
    }
}

/// <summary>
/// Remembers where the window was: its size (logical pixels), its top-left (physical, virtual-desktop pixels,
/// which survive monitors with different DPI), whether it was maximized and on which monitor. A position no longer on any monitor
/// is ignored, so a disconnected screen never strands the window off-screen.
/// </summary>
internal static class WindowPlacement
{
    private static readonly SettingsJsonContext Json = SettingsJsonContext.Default;

    /// <summary>The size to open at, or null for the default.</summary>
    public static Size? SavedSize
    {
        get
        {
            int width = LocalStorage.Get("windowWidth", 0, Json.Int32);
            int height = LocalStorage.Get("windowHeight", 0, Json.Int32);
            return width >= 400 && height >= 300 ? new Size(width, height) : null;
        }
    }

    public static void Restore()
    {
        if (!LocalStorage.Exists("windowX"))
        {
            return;
        }

        int x = LocalStorage.Get("windowX", 0, Json.Int32);
        int y = LocalStorage.Get("windowY", 0, Json.Int32);
        // The title bar's left end must land on a monitor that is still there.
        var titleBar = new Point(x + 40, y + 20);
        bool onScreen = App.Screens.All.Any(screen => screen.Bounds.Contains(titleBar));
        if (onScreen)
        {
            App.Window.SetScreenPosition(x, y);
        }

        if (!LocalStorage.Get("windowMaximized", false, Json.Boolean))
        {
            return;
        }

        // Maximized on another monitor than the restored place (Win+Shift+arrow, or dragged there maximized):
        // open on that monitor, near its top-left, so it maximizes there and un-maximizes there too.
        var maximizedAt = new Point(
            LocalStorage.Get("windowMaximizedX", x + 40, Json.Int32), LocalStorage.Get("windowMaximizedY", y + 20, Json.Int32));
        ScreenInfo? monitor = App.Screens.All.FirstOrDefault(screen => screen.Bounds.Contains(maximizedAt));
        if (monitor is not null && !(onScreen && monitor.Bounds.Contains(titleBar)))
        {
            App.Window.SetScreenPosition((int)monitor.WorkArea.X + 40, (int)monitor.WorkArea.Y + 40);
        }
        App.Window.Maximize();
    }

    public static void Save()
    {
        // Fullscreen and minimized are moments, not places to come back to.
        if (App.Window.IsFullScreen || App.Window.IsMinimized)
        {
            return;
        }

        bool maximized = App.Window.IsMaximized;
        LocalStorage.Set("windowMaximized", maximized, Json.Boolean);
        if (maximized)
        {
            // Keep the restored size and place from before it was maximized; note only which monitor it fills.
            Rect filled = App.Window.ScreenBounds;
            LocalStorage.Set("windowMaximizedX", (int)(filled.X + filled.Width / 2), Json.Int32);
            LocalStorage.Set("windowMaximizedY", (int)(filled.Y + filled.Height / 2), Json.Int32);
            return;
        }

        // The client size, which is what the window is created at; the outer size would grow by the frame each run.
        Rect client = App.Window.ClientBounds;
        Rect screen = App.Window.ScreenBounds;
        // Rounded: at a fractional DPI scale the logical size isn't whole, and truncating shrank it a pixel a run.
        LocalStorage.Set("windowWidth", (int)MathF.Round(client.Width), Json.Int32);
        LocalStorage.Set("windowHeight", (int)MathF.Round(client.Height), Json.Int32);
        LocalStorage.Set("windowX", (int)screen.X, Json.Int32);
        LocalStorage.Set("windowY", (int)screen.Y, Json.Int32);
    }
}

[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(float))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
