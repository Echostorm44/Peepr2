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
/// which survive monitors with different DPI) and whether it was maximized. A position no longer on any monitor
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
        bool onScreen = App.Screens.All.Any(screen => screen.Bounds.Contains(new Point(x + 40, y + 20)));
        if (onScreen)
        {
            App.Window.SetScreenPosition(x, y);
        }
        if (LocalStorage.Get("windowMaximized", false, Json.Boolean))
        {
            App.Window.Maximize();
        }
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
            return; // keep the restored size and place from before it was maximized
        }

        // The client size, which is what the window is created at; the outer size would grow by the frame each run.
        Rect client = App.Window.ClientBounds;
        Rect screen = App.Window.ScreenBounds;
        LocalStorage.Set("windowWidth", (int)client.Width, Json.Int32);
        LocalStorage.Set("windowHeight", (int)client.Height, Json.Int32);
        LocalStorage.Set("windowX", (int)screen.X, Json.Int32);
        LocalStorage.Set("windowY", (int)screen.Y, Json.Int32);
    }
}

[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(float))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
