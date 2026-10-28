using Cascade.UI;

namespace Peepr;

/// <summary>
/// The Lucide icons Peepr uses (lucide.dev, ISC licence), on Lucide's 24×24 grid. Lucide's circle and rect
/// primitives are written out as arcs, because <see cref="Icon"/> takes path data.
/// </summary>
internal static class LucideIcons
{
    private static readonly Size Grid = new(24, 24);

    private static Icon Create(string name, params string[] paths) => new(paths, Grid, 20f, name);

    public static readonly Icon ChevronLeft = Create("Previous", "m15 18-6-6 6-6");

    public static readonly Icon ChevronRight = Create("Next", "m9 18 6-6-6-6");

    public static readonly Icon Play = Create("Play", "M6 3l14 9-14 9V3z");

    public static readonly Icon Pause = Create("Pause",
        "M15 4h2a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1h-2a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1z",
        "M7 4h2a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1z");

    // Lucide "presentation": the slideshow toggle.
    public static readonly Icon Slideshow = Create("Slideshow",
        "M2 3h20",
        "M21 3v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V3",
        "m7 21 5-5 5 5");

    public static readonly Icon RotateClockwise = Create("Rotate",
        "M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8",
        "M21 3v5h-5");

    // Lucide "rotate-ccw": play a finished video again.
    public static readonly Icon Replay = Create("Play again",
        "M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8",
        "M3 3v5h5");

    public static readonly Icon Copy = Create("Copy",
        "M10 8h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H10a2 2 0 0 1-2-2V10a2 2 0 0 1 2-2z",
        "M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2");

    public static readonly Icon Trash = Create("Delete",
        "M3 6h18",
        "M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6",
        "M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2",
        "M10 11v6",
        "M14 11v6");

    public static readonly Icon FolderOpen = Create("Open",
        "m6 14 1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2");

    public static readonly Icon Settings = Create("Settings",
        "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z",
        "M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0z");

    public static readonly Icon Maximize = Create("Full screen",
        "M8 3H5a2 2 0 0 0-2 2v3",
        "M21 8V5a2 2 0 0 0-2-2h-3",
        "M3 16v3a2 2 0 0 0 2 2h3",
        "M16 21h3a2 2 0 0 0 2-2v-3");

    public static readonly Icon Minimize = Create("Exit full screen",
        "M8 3v3a2 2 0 0 1-2 2H3",
        "M21 8h-3a2 2 0 0 1-2-2V3",
        "M3 16h3a2 2 0 0 1 2 2v3",
        "M16 21v-3a2 2 0 0 1 2-2h3");

    public static readonly Icon Volume = Create("Mute",
        "M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z",
        "M16 9a5 5 0 0 1 0 6",
        "M19.364 18.364a9 9 0 0 0 0-12.728");

    public static readonly Icon VolumeMuted = Create("Unmute",
        "M11 4.702a.705.705 0 0 0-1.203-.498L6.413 7.587A1.4 1.4 0 0 1 5.416 8H3a1 1 0 0 0-1 1v6a1 1 0 0 0 1 1h2.416a1.4 1.4 0 0 1 .997.413l3.383 3.384A.705.705 0 0 0 11 19.298z",
        "m22 9-6 6",
        "m16 9 6 6");

    public static readonly Icon Thumbnails = Create("Thumbnails",
        "M2 7v10",
        "M6 5v14",
        "M12 3h8a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-8a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z");

    public static readonly Icon Film = Create("Video",
        "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z",
        "M7 3v18",
        "M3 7.5h4",
        "M3 12h18",
        "M3 16.5h4",
        "M17 3v18",
        "M17 7.5h4",
        "M17 16.5h4");

    public static readonly Icon Undo = Create("Undo",
        "M9 14 4 9l5-5",
        "M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5a5.5 5.5 0 0 1-5.5 5.5H11");

    public static readonly Icon Close = Create("Close", "M18 6 6 18", "m6 6 12 12");

    public static readonly Icon ImageOff = Create("Can't open",
        "m2 2 20 20",
        "M10.41 10.41a2 2 0 1 1-2.83-2.83",
        "M13.5 13.5 6 21",
        "M18 12l3 3",
        "M3.59 3.59A1.99 1.99 0 0 0 3 5v14a2 2 0 0 0 2 2h14c.55 0 1.052-.22 1.41-.59",
        "M21 15V5a2 2 0 0 0-2-2H9");
}
