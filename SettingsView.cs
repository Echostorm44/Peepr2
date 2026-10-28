using Cascade.UI;

namespace Peepr;

/// <summary>The Settings dialog. Every change applies and saves at once; there is no OK button to forget.</summary>
internal sealed class SettingsView : Component
{
    private static readonly ColorValue TextPrimary = new("#FFFFFF");
    private static readonly ColorValue TextSecondary = new("#98989E");
    private static readonly ColorValue Hairline = new("#3A3A3C");

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
    private bool confirmBeforeDelete = PeeprSettings.ConfirmBeforeDelete;

    public SettingsView(Action onChanged)
    {
        this.onChanged = onChanged;
    }

    protected override Node Render() =>
        new Column(
            spacing: 14,
            children:
            [
                Setting("Sort files by",
                    new Select<SortOrder>(Bind(sortOrder, value =>
                    {
                        sortOrder = value;
                        PeeprSettings.SortOrder = value;
                        onChanged();
                    }), SortOptions).Width(250)),
                Separator(),
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
                Separator(),
                new Toggle(
                    Bind(confirmBeforeDelete, value =>
                    {
                        confirmBeforeDelete = value;
                        PeeprSettings.ConfirmBeforeDelete = value;
                    }),
                    "Ask before deleting",
                    "Deleted files always go to the Recycle Bin, and Ctrl+Z brings them back"),
            ])
            .Width(460)
            .Padding(EdgeInsets.All(4));

    private static Node Setting(string label, Node control) =>
        new Row(
            crossAxisAlignment: CrossAxisAlignment.Center,
            children: [new Label(label).FontSize(14).Color(TextPrimary).Expand(), control]);

    private static Node Separator() =>
        new Column(children: []).Height(1).Background(Hairline);
}
