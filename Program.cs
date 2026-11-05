using Cascade.UI;
using Cascade.UI.Installer.Update;
using Peepr;

// Updates come from GitHub Releases (a v* tag builds, signs and publishes one). Nothing is downloaded or applied
// without asking: MainView checks in the background and offers a newer version in a banner. Crash rollback after
// an update belongs to the update shim; the app's only part is Updater.MarkHealthy() once it is up. Best effort:
// a failure here never stops Peepr.
try
{
    string installDir = AppContext.BaseDirectory;
    string appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}"
        : "2.0.1";

    Updater.Configure(
        new UpdateConfig
        {
            ManifestUrl = "https://github.com/Echostorm44/Peepr2/releases/latest/download/manifest.json",
            CheckOnStartup = true,
            CheckInterval = TimeSpan.FromHours(6),
            AutoDownload = false,
            Channel = "stable",
        },
        appVersion,
        "win-x64",
        installDir,
        Environment.ProcessPath ?? System.IO.Path.Combine(installDir, "Peepr.exe"));
}
catch
{
    // The app runs regardless.
}

// Files passed on the command line (Explorer "Open with", a file association). App.Args isn't populated until
// App.Run starts, so read the raw command line; anything that isn't a flag is a path.
FileIntake.QueueInitial(Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith('-')));

// Opening another file while Peepr is running shows it in this window rather than starting a second one.
App.OnSecondInstanceLaunched(args => FileIntake.Receive(args.Where(a => !a.StartsWith('-'))));
App.OnFilesDropped(FileIntake.Receive);

App.Run<MainView>(config =>
{
    config.Theme = new AppleTheme(ThemeMode.Dark);
    config.SingleInstance = true;
    config.WindowSize = WindowPlacement.SavedSize ?? new Size(1280, 800);
});

namespace Peepr
{
    /// <summary>
    /// Hands files from the OS (launch arguments, Explorer drops, a second launch) to <see cref="MainView"/>. Launch
    /// arguments arrive before the view exists, so they wait in <see cref="TakePending"/>; later arrivals raise
    /// <see cref="FilesReceived"/> on the UI thread.
    /// </summary>
    internal static class FileIntake
    {
        private static readonly List<string> pending = [];

        public static event Action<IReadOnlyList<string>>? FilesReceived;

        public static void QueueInitial(IEnumerable<string> paths) => pending.AddRange(Existing(paths));

        public static IReadOnlyList<string> TakePending()
        {
            string[] taken = [.. pending];
            pending.Clear();
            return taken;
        }

        public static void Receive(IEnumerable<string> paths)
        {
            List<string> existing = Existing(paths);
            if (existing.Count > 0)
            {
                FilesReceived?.Invoke(existing);
            }
        }

        private static List<string> Existing(IEnumerable<string> paths) =>
            paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
    }
}
