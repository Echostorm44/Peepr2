using Cascade.UI;

namespace Peepr;

/// <summary>The Settings dialog. Every change applies and saves at once; there is no OK button to forget.</summary>
internal sealed class SettingsView : Component
{
    private static readonly ColorValue TextPrimary = new("#FFFFFF");
    private static readonly ColorValue TextSecondary = new("#98989E");
    private static readonly ColorValue Hairline = new("#3A3A3C");
    private static readonly ColorValue GroupBackground = new("#2C2C2E");

    private static readonly IReadOnlyList<SelectOption<SortOrder>> SortOptions =
    [
        new(SortOrder.Name, "Name"),
        new(SortOrder.NewestFirst, "Date modified, newest first"),
        new(SortOrder.OldestFirst, "Date modified, oldest first"),
        new(SortOrder.LargestFirst, "Size, largest first"),
        new(SortOrder.Type, "Type"),
    ];

    private readonly Action onChanged;

    private SortOrder sortOrder = PeeprSettings.SortOrder;
    private int slideshowSeconds = PeeprSettings.SlideshowSeconds;
    private bool showThumbnailStrip = PeeprSettings.ShowThumbnailStrip;
    private bool confirmBeforeDelete = PeeprSettings.ConfirmBeforeDelete;
    private FileAssociations.Status associationStatus = FileAssociations.GetStatus(FileAssociations.CurrentExePath);
    private string? associationError;

    public SettingsView(Action onChanged)
    {
        this.onChanged = onChanged;
    }

    protected override Node Render() =>
        new Column(
            spacing: 18,
            children:
            [
                Section("Viewing",
                    Setting("Sort files by",
                        new Select<SortOrder>(Bind(sortOrder, value =>
                        {
                            sortOrder = value;
                            PeeprSettings.SortOrder = value;
                            onChanged();
                        }), SortOptions).Width(250)),
                    Setting("Slideshow delay",
                        new Row(
                            spacing: 8,
                            crossAxisAlignment: CrossAxisAlignment.Center,
                            children:
                            [
                                new NumberInput<int>(
                                    Bind(slideshowSeconds, value =>
                                    {
                                        slideshowSeconds = value;
                                        PeeprSettings.SlideshowSeconds = value;
                                    }),
                                    min: PeeprSettings.MinimumSlideshowSeconds,
                                    max: PeeprSettings.MaximumSlideshowSeconds,
                                    step: 1,
                                    format: "0").Width(90),
                                new Label("seconds").FontSize(13).Color(TextSecondary),
                            ])),
                    new Toggle(
                        Bind(showThumbnailStrip, value =>
                        {
                            showThumbnailStrip = value;
                            PeeprSettings.ShowThumbnailStrip = value;
                            onChanged();
                        }),
                        "Thumbnail strip",
                        "Show the folder's photos and videos along the bottom (T)")),
                Section("Deleting",
                    new Toggle(
                        Bind(confirmBeforeDelete, value =>
                        {
                            confirmBeforeDelete = value;
                            PeeprSettings.ConfirmBeforeDelete = value;
                        }),
                        "Ask before deleting",
                        "Deleted files always go to the Recycle Bin, and Ctrl+Z brings them back")),
                Section("File types", FileTypes()),
                new Label($"Peepr {AppVersion} · free and open source").FontSize(12).Color(TextSecondary),
            ])
            .Width(540)
            .Padding(EdgeInsets.All(4));

    private Node FileTypes()
    {
        string status = associationStatus switch
        {
            FileAssociations.Status.Registered => "Peepr is in the Open with menu for photos and videos.",
            FileAssociations.Status.RegisteredElsewhere => "Registered for a copy of Peepr in another folder. Register again to use this one.",
            _ => "Peepr isn't registered with Windows yet.",
        };
        bool registered = associationStatus == FileAssociations.Status.Registered;

        var children = new List<Node>
        {
            new Label(associationError ?? status).FontSize(13).Color(associationError is null ? TextSecondary : new ColorValue("#FF453A")),
            new Row(
                spacing: 10,
                children:
                [
                    registered
                        ? new Button("Unregister", () => ChangeAssociations(register: false)).Variant("ghost")
                        : new Button("Register", () => ChangeAssociations(register: true)),
                    new Button("Set as default…", FileAssociations.OpenDefaultAppsSettings)
                        .Variant("ghost")
                        .Disabled(!registered)
                        .Tooltip("Opens Peepr's page in Windows Settings → Default apps"),
                ]),
        };
        return new Column(spacing: 10, children: [.. children]);
    }

    private void ChangeAssociations(bool register)
    {
        try
        {
            if (register)
            {
                FileAssociations.Register(FileAssociations.CurrentExePath);
            }
            else
            {
                FileAssociations.Unregister();
            }
            associationError = null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            associationError = "Windows didn't allow the change: " + ex.Message;
        }
        associationStatus = FileAssociations.GetStatus(FileAssociations.CurrentExePath);
        Invalidate();
    }

    private static string AppVersion =>
        typeof(SettingsView).Assembly.GetName().Version is { } version ? $"{version.Major}.{version.Minor}.{version.Build}" : "";

    private static Node Section(string title, params Node[] rows)
    {
        var children = new List<Node>(rows.Length * 2);
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                children.Add(Separator());
            }
            children.Add(rows[i]);
        }

        return new Column(
            spacing: 8,
            children:
            [
                new Label(title).FontSize(12).Color(TextSecondary),
                new Column(spacing: 12, children: [.. children])
                    .Padding(EdgeInsets.All(14))
                    .Background(GroupBackground)
                    .CornerRadius(12),
            ]);
    }

    private static Node Setting(string label, Node control) =>
        new Row(
            crossAxisAlignment: CrossAxisAlignment.Center,
            children: [new Label(label).Color(TextPrimary).Expand(), control]);

    private static Node Separator() =>
        new Column(children: []).Height(1).Background(Hairline);
}
