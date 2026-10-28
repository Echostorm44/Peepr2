using Cascade.UI;

namespace Peepr;

/// <summary>The user's preferences, persisted through Cascade's <see cref="LocalStorage"/> as plain values.</summary>
internal static class PeeprSettings
{
    public const int MinimumSlideshowSeconds = 1;
    public const int MaximumSlideshowSeconds = 120;

    public static int SlideshowSeconds
    {
        get => Math.Clamp(LocalStorage.Get("slideshowSeconds", 5), MinimumSlideshowSeconds, MaximumSlideshowSeconds);
        set => LocalStorage.Set("slideshowSeconds", Math.Clamp(value, MinimumSlideshowSeconds, MaximumSlideshowSeconds));
    }

    public static SortOrder SortOrder
    {
        get => (SortOrder)Math.Clamp(LocalStorage.Get("sortOrder", 0), 0, (int)SortOrder.Type);
        set => LocalStorage.Set("sortOrder", (int)value);
    }

    public static bool ConfirmBeforeDelete
    {
        get => LocalStorage.Get("confirmBeforeDelete", false);
        set => LocalStorage.Set("confirmBeforeDelete", value);
    }

    public static bool ShowThumbnailStrip
    {
        get => LocalStorage.Get("showThumbnailStrip", false);
        set => LocalStorage.Set("showThumbnailStrip", value);
    }

    public static float Volume
    {
        get => Math.Clamp(LocalStorage.Get("volume", 0.8f), 0f, 1f);
        set => LocalStorage.Set("volume", Math.Clamp(value, 0f, 1f));
    }

    public static bool Muted
    {
        get => LocalStorage.Get("muted", false);
        set => LocalStorage.Set("muted", value);
    }
}
