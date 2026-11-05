using Cascade.UI.Installer;

namespace Peepr;

/// <summary>
/// The install/uninstall declaration for Peepr, consumed by <c>cascade package --installer</c> to build the
/// single-file setup exe. A per-user install (no admin): files under LocalAppData, shortcuts on the Start Menu
/// and Desktop, and Peepr added to Open with for its photo and video types — through the same registration
/// Settings → File types uses, so the two never disagree. Uninstall removes all of it.
/// </summary>
// Must be PUBLIC: `cascade package --installer` finds it by the public modifier and the generated wizard
// instantiates it from a separate assembly.
[Installer]
public sealed class PeeprInstaller : CascadeInstaller
{
    public override InstallerConfig Configure() => new()
    {
        AppId = "6F2C8E41-93B7-4D5A-A0E2-7C1B9D46F358",
        AppName = "Peepr",                       // drives the exe name (Peepr.exe) and install folder
        Version = ResolveVersion(),
        Publisher = "Echostorm",
        Description = "A fast, simple viewer for photos and videos.",
        InstallDir = InstallDir.LocalAppData("Peepr"),
        Output = "Peepr-Setup",
        RequiresAdmin = false,                   // per-user: LocalAppData + HKCU only
        Shortcuts =
        [
            new Shortcut { Name = "Peepr", TargetPath = "Peepr.exe", Location = ShortcutLocation.StartMenu },
            new Shortcut { Name = "Peepr", TargetPath = "Peepr.exe", Location = ShortcutLocation.Desktop },
        ],
    };

    public override IReadOnlyList<InstallFile> Files =>
    [
        // The whole published app (Peepr.exe + the native renderer libraries and fonts) staged by the packager.
        InstallFile.Directory("publish/*", dest: Dir.App, recursive: true),
    ];

    public override Task OnInstallAsync(InstallContext ctx)
    {
        FileAssociations.Register(Path.Combine(ctx.InstallDir, "Peepr.exe"));

        // The uninstall.exe that ships with Peepr doesn't run this class, so record what registration wrote for
        // it to remove: Peepr's own keys whole, and only Peepr's values in the keys file types share.
        foreach (string key in FileAssociations.OwnedKeys())
        {
            ctx.RecordRegistryKey(key);
        }
        foreach ((string key, string name) in FileAssociations.SharedValues())
        {
            ctx.RecordRegistryValue(key, name);
        }
        return Task.CompletedTask;
    }

    // Uninstalling through the setup wizard runs this; uninstall.exe removes the recorded entries instead.
    public override Task OnUninstallAsync(InstallContext ctx)
    {
        FileAssociations.Unregister();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The app assembly's version as <c>major.minor.patch</c>. CI stamps the tag version into the build, so the
    /// setup, the Add/Remove Programs entry and upgrade detection all report the released version.
    /// </summary>
    private static string ResolveVersion()
    {
        Version? version = typeof(PeeprInstaller).Assembly.GetName().Version;
        return version is null ? "2.0.1" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
