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

[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(float))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
