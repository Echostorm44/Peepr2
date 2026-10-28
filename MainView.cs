using Cascade.UI;
using KeyCode = Cascade.UI.Key;
using Path = System.IO.Path;

namespace Peepr;

/// <summary>
/// Peepr's one window: the picture or video, edge to edge, with a floating toolbar that fades out while you look
/// and comes back when the pointer moves. Files arrive from the command line, Explorer drops, a second launch, or
/// the Open button; everything in the same folder is then a keypress away.
/// </summary>
internal sealed class MainView : Component
{
    // ── Palette (Apple dark) ──────────────────────────────────────────
    private static readonly ColorValue StageBackground = new("#1E1E1E");
    private static readonly ColorValue FullScreenBackground = new("#000000");
    private static readonly ColorValue ToolbarBackground = new("#2C2C2EE8");
    private static readonly ColorValue ToolbarHairline = new("#FFFFFF1A");
    private static readonly ColorValue Divider = new("#FFFFFF24");
    private static readonly ColorValue TextPrimary = new("#FFFFFF");
    private static readonly ColorValue TextSecondary = new("#98989E");
    private static readonly ColorValue Accent = new("#0A84FF");

    private const int ChromeIdleHideMs = 2500;
    private const int NoticeVisibleMs = 4000;

    private readonly record struct DeletedFile(string Path, int Index);

    private readonly PictureCache pictureCache = new();
    private readonly PictureStage stage = new();
    private readonly ImageSource? wallpaper = LoadWallpaper();
    private readonly Stack<DeletedFile> deletedFiles = new();

    private MediaFolder? folder;
    private string? openError;
    private bool loading;
    private int showGeneration;

    private bool chromeVisible = true;
    private bool chromeTimerRunning;
    private long lastActivityTicks;

    private bool slideshowRunning;
    private int slideshowGeneration;

    private string? notice;
    private bool noticeOffersUndo;
    private int noticeGeneration;

    protected override Task OnMounted()
    {
        FileIntake.FilesReceived += OpenPaths;
        IReadOnlyList<string> launchFiles = FileIntake.TakePending();
        if (launchFiles.Count > 0)
        {
            OpenPaths(launchFiles);
        }
        return Task.CompletedTask;
    }

    protected override void OnUnmounted()
    {
        FileIntake.FilesReceived -= OpenPaths;
        stage.Clear();
        wallpaper?.Dispose();
    }

    private static ImageSource? LoadWallpaper()
    {
        using Stream? stream = typeof(MainView).Assembly.GetManifestResourceStream("PeeprBackDrop.png");
        return stream is null ? null : ImageSource.FromStream(stream);
    }

    // ── Opening and showing files ─────────────────────────────────────

    private void OpenPaths(IReadOnlyList<string> paths)
    {
        string first = paths[0];
        folder = Directory.Exists(first)
            ? MediaFolder.OpenDirectory(first, PeeprSettings.SortOrder)
            : MediaFolder.Open(first, PeeprSettings.SortOrder);
        _ = ShowCurrentAsync();
    }

    private async Task ShowCurrentAsync()
    {
        int generation = ++showGeneration;
        openError = null;
        string? file = folder?.CurrentFile;
        UpdateWindowTitle();
        if (file is null)
        {
            stage.Clear();
            loading = false;
            Invalidate();
            return;
        }

        if (MediaTypes.KindOf(file) == MediaKind.Video)
        {
            stage.Clear();
            loading = false;
            openError = "Video playback arrives in the next build.";
            Invalidate();
            PrefetchNeighbours();
            return;
        }

        Task<DecodedPicture> decode = pictureCache.Get(file);
        if (!decode.IsCompleted)
        {
            stage.Clear();
            loading = true;
            Invalidate();
        }

        try
        {
            DecodedPicture picture = await decode;
            if (generation != showGeneration)
            {
                return;
            }
            stage.Show(picture);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation != showGeneration)
            {
                return;
            }
            stage.Clear();
            pictureCache.Forget(file);
            openError = DescribeOpenFailure(ex);
        }

        loading = false;
        Invalidate();
        PrefetchNeighbours();
    }

    // The taskbar and Alt+Tab name the file on screen and where it sits in the folder.
    private void UpdateWindowTitle()
    {
        string? file = folder?.CurrentFile;
        App.Window.Title = file is null
            ? "Peepr"
            : $"{Path.GetFileName(file)} — {folder!.CurrentIndex + 1} of {folder.Count}";
    }

    private static string DescribeOpenFailure(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "This file is no longer there.",
        UnauthorizedAccessException => "Windows won't let Peepr read this file.",
        NotSupportedException => "This isn't a format Peepr can show.",
        _ => "This file looks damaged or incomplete.",
    };

    // Keeps the pictures either side decoded, and only those, so arrowing through a folder is instant.
    private void PrefetchNeighbours()
    {
        if (folder is null)
        {
            return;
        }

        string? next = folder.FileAt(1);
        string? previous = folder.FileAt(-1);
        pictureCache.RetainOnly([folder.CurrentFile, next, previous]);
        pictureCache.Prefetch(next);
        pictureCache.Prefetch(previous);
    }

    private void Navigate(int offset)
    {
        if (folder is null || folder.Count < 2)
        {
            return;
        }
        folder.MoveBy(offset);
        _ = ShowCurrentAsync();
    }

    private void NavigateTo(int index)
    {
        if (folder is null || folder.IsEmpty)
        {
            return;
        }
        folder.MoveTo(index);
        _ = ShowCurrentAsync();
    }

    private async Task OpenWithPickerAsync()
    {
        FilePickerResult? result = await FilePicker.OpenAsync(
            title: "Open",
            filters:
            [
                new FileFilter("Photos and videos", [.. MediaTypes.ImageExtensions.Concat(MediaTypes.VideoExtensions).Select(e => "*" + e)]),
                new FileFilter("All files", "*.*"),
            ],
            initialDirectory: folder?.Directory);
        if (result is not null)
        {
            OpenPaths([result.Path]);
        }
    }

    // Opens a whole folder, starting from its first file in the chosen sort order.
    private async Task OpenFolderWithPickerAsync()
    {
        string? directory = await FolderPicker.OpenAsync("Open folder", folder?.Directory);
        if (directory is not null)
        {
            OpenPaths([directory]);
        }
    }

    private void ShowOpenMenu() =>
        ContextMenu.Show(
        [
            ContextMenuItem.Action("Open file…", () => _ = OpenWithPickerAsync(), shortcut: "Ctrl+O"),
            ContextMenuItem.Action("Open folder…", () => _ = OpenFolderWithPickerAsync(), shortcut: "Ctrl+Shift+O"),
        ]);

    private void ShowPictureMenu()
    {
        bool hasFile = folder?.CurrentFile is not null;
        ContextMenu.Show(
        [
            ContextMenuItem.Action("Copy", () => _ = CopyCurrentAsync(), shortcut: "Ctrl+C", disabled: !hasFile),
            ContextMenuItem.Action("Rotate", Rotate, shortcut: "R", disabled: !stage.HasPicture),
            ContextMenuItem.Action("Show in File Explorer", ShowInExplorer, disabled: !hasFile),
            ContextMenuItem.Separator(),
            ContextMenuItem.Action("Delete", () => _ = DeleteCurrentAsync(), MenuItemStyle.Destructive, shortcut: "Del", disabled: !hasFile),
            ContextMenuItem.Separator(),
            ContextMenuItem.Action("Open file…", () => _ = OpenWithPickerAsync(), shortcut: "Ctrl+O"),
            ContextMenuItem.Action("Open folder…", () => _ = OpenFolderWithPickerAsync(), shortcut: "Ctrl+Shift+O"),
        ]);
    }

    private void ShowInExplorer()
    {
        if (folder?.CurrentFile is { } file)
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"");
        }
    }

    private void ToggleFullScreen()
    {
        App.Window.ToggleFullScreen();
        Invalidate();
    }

    // ── Actions ───────────────────────────────────────────────────────

    private async Task DeleteCurrentAsync()
    {
        string? file = folder?.CurrentFile;
        if (folder is null || file is null)
        {
            return;
        }

        if (PeeprSettings.ConfirmBeforeDelete)
        {
            bool confirmed = await Dialog.ConfirmAsync(
                "Delete this file?",
                $"Do you really want to delete this cute innocent file, you monster?\n\n{Path.GetFileName(file)} will go to the Recycle Bin.",
                confirmLabel: "Delete",
                cancelLabel: "Keep it",
                defaultButton: DialogDefault.Cancel,
                style: DialogStyle.Destructive);
            if (!confirmed || folder?.CurrentFile != file)
            {
                return;
            }
        }

        bool recycled = await Task.Run(() => RecycleBin.Send(file));
        if (!recycled)
        {
            ShowNotice($"Couldn't delete {Path.GetFileName(file)}. Is it open somewhere else?", offerUndo: false);
            return;
        }

        if (folder?.CurrentFile == file && folder.RemoveCurrent() is { } removed)
        {
            deletedFiles.Push(new DeletedFile(removed.Path, removed.Index));
        }
        pictureCache.Forget(file);
        ShowNotice($"Moved {Path.GetFileName(file)} to the Recycle Bin", offerUndo: true);
        await ShowCurrentAsync();
    }

    private async Task UndoDeleteAsync()
    {
        if (!deletedFiles.TryPop(out DeletedFile deleted))
        {
            return;
        }

        bool restored = await Task.Run(() => RecycleBin.TryRestore(deleted.Path));
        if (!restored)
        {
            ShowNotice($"{Path.GetFileName(deleted.Path)} is no longer in the Recycle Bin.", offerUndo: false);
            return;
        }

        bool sameFolder = folder is not null && string.Equals(
            Path.GetDirectoryName(deleted.Path), folder.Directory, StringComparison.OrdinalIgnoreCase);
        if (sameFolder)
        {
            folder!.Restore(deleted.Path, deleted.Index);
        }
        else
        {
            folder = MediaFolder.Open(deleted.Path, PeeprSettings.SortOrder);
        }
        ShowNotice($"Restored {Path.GetFileName(deleted.Path)}", offerUndo: false);
        await ShowCurrentAsync();
    }

    // Puts both the picture (to paste into an editor or chat) and the file (to paste into Explorer) on the clipboard.
    private async Task CopyCurrentAsync()
    {
        string? file = folder?.CurrentFile;
        if (file is null)
        {
            return;
        }

        ImageData? image = null;
        if (MediaTypes.KindOf(file) == MediaKind.Image && stage.HasPicture)
        {
            try
            {
                DecodedPicture picture = await pictureCache.Get(file);
                image = new ImageData { Pixels = picture.Frames[0], Width = picture.Width, Height = picture.Height, Stride = picture.Width * 4 };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The file alone still copies.
            }
        }

        bool copied = await Clipboard.WriteAsync(new ClipboardContent { Image = image, Files = [file] });
        ShowNotice(copied ? "Copied" : "Couldn't copy to the clipboard", offerUndo: false);
    }

    private void Rotate()
    {
        if (stage.HasPicture)
        {
            stage.Rotate();
            Invalidate();
        }
    }

    private void Zoom(float factor)
    {
        stage.ZoomBy(factor);
        Invalidate();
    }

    private void ResetZoom()
    {
        stage.ResetZoom();
        Invalidate();
    }

    private void ToggleSlideshow()
    {
        if (slideshowRunning)
        {
            StopSlideshow();
            return;
        }
        if (folder is null || folder.Count < 2)
        {
            return;
        }

        slideshowRunning = true;
        _ = RunSlideshowAsync(++slideshowGeneration);
        Invalidate();
    }

    private void StopSlideshow()
    {
        slideshowRunning = false;
        slideshowGeneration++;
        Invalidate();
    }

    private async Task RunSlideshowAsync(int generation)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(PeeprSettings.SlideshowSeconds), LifetimeToken).ConfigureAwait(true);
            if (generation != slideshowGeneration || !slideshowRunning)
            {
                return;
            }
            Navigate(1);
        }
    }

    private Task OpenSettingsAsync() => Dialog.ShowAsync(new SettingsView(OnSettingsChanged), new DialogOptions { Title = "Settings" });

    private void OnSettingsChanged()
    {
        folder?.Resort(PeeprSettings.SortOrder);
        PrefetchNeighbours();
        Invalidate();
    }

    // Esc backs out one level at a time: fullscreen (and its slideshow), slideshow, zoom, then the app.
    private void HandleEscape()
    {
        if (App.Window.IsFullScreen)
        {
            if (slideshowRunning)
            {
                StopSlideshow();
            }
            ToggleFullScreen();
            return;
        }
        if (slideshowRunning)
        {
            StopSlideshow();
            return;
        }
        if (stage.IsZoomed)
        {
            ResetZoom();
            return;
        }
        App.Window.Close();
    }

    // ── Notices ───────────────────────────────────────────────────────

    private void ShowNotice(string message, bool offerUndo)
    {
        notice = message;
        noticeOffersUndo = offerUndo;
        int generation = ++noticeGeneration;
        Invalidate();
        _ = HideNoticeLaterAsync(generation);
    }

    private async Task HideNoticeLaterAsync(int generation)
    {
        await Task.Delay(NoticeVisibleMs, LifetimeToken).ConfigureAwait(true);
        if (generation == noticeGeneration)
        {
            notice = null;
            Invalidate();
        }
    }

    // ── Chrome visibility ─────────────────────────────────────────────

    // Any pointer movement shows the chrome; it fades again once the pointer has rested for a moment.
    private void OnPointerActivity()
    {
        lastActivityTicks = Environment.TickCount64;
        if (!chromeVisible)
        {
            chromeVisible = true;
            Invalidate();
        }
        if (!chromeTimerRunning)
        {
            chromeTimerRunning = true;
            _ = HideChromeWhenIdleAsync();
        }
    }

    private async Task HideChromeWhenIdleAsync()
    {
        try
        {
            while (true)
            {
                long idle = Environment.TickCount64 - lastActivityTicks;
                if (idle >= ChromeIdleHideMs)
                {
                    break;
                }
                await Task.Delay((int)(ChromeIdleHideMs - idle), LifetimeToken).ConfigureAwait(true);
            }

            if (stage.HasPicture)
            {
                chromeVisible = false;
                Invalidate();
            }
        }
        finally
        {
            chromeTimerRunning = false;
        }
    }

    // ── Render ────────────────────────────────────────────────────────

    protected override Node Render()
    {
        Node content = folder is null || folder.IsEmpty ? Welcome() : Viewer();
        var layers = new List<Node> { content };
        if (folder is not null && !folder.IsEmpty)
        {
            layers.Add(new AnimatePresence(chromeVisible, SideArrows()).PassThroughHits());
        }
        layers.Add(new AnimatePresence(chromeVisible || folder is null, ToolbarLayer()).PassThroughHits());
        if (notice is not null)
        {
            layers.Add(NoticeLayer().PassThroughHits());
        }

        return new KeyHandler(
            new Stack([.. layers]).Background(App.Window.IsFullScreen ? FullScreenBackground : StageBackground),
            KeyBindings());
    }

    private KeyBinding[] KeyBindings() =>
    [
        Bind(KeyCode.Left, () => Navigate(-1)),
        Bind(KeyCode.PageUp, () => Navigate(-1)),
        Bind(KeyCode.Right, () => Navigate(1)),
        Bind(KeyCode.PageDown, () => Navigate(1)),
        Bind(KeyCode.Space, () => Navigate(1)),
        Bind(KeyCode.Home, () => NavigateTo(0)),
        Bind(KeyCode.End, () => NavigateTo(int.MaxValue)),
        Bind(KeyCode.Delete, () => _ = DeleteCurrentAsync(), allowRepeat: false),
        Bind(KeyCode.Z, () => _ = UndoDeleteAsync(), ModifierKeys.Ctrl, allowRepeat: false),
        Bind(KeyCode.C, () => _ = CopyCurrentAsync(), ModifierKeys.Ctrl, allowRepeat: false),
        Bind(KeyCode.BrowserBack, () => Navigate(-1)),
        Bind(KeyCode.BrowserForward, () => Navigate(1)),
        Bind(KeyCode.F11, ToggleFullScreen, allowRepeat: false),
        Bind(KeyCode.F, ToggleFullScreen, allowRepeat: false),
        Bind(KeyCode.O, () => _ = OpenWithPickerAsync(), ModifierKeys.Ctrl, allowRepeat: false),
        Bind(KeyCode.O, () => _ = OpenFolderWithPickerAsync(), ModifierKeys.Ctrl | ModifierKeys.Shift, allowRepeat: false),
        Bind(KeyCode.Comma, () => _ = OpenSettingsAsync(), ModifierKeys.Ctrl, allowRepeat: false),
        Bind(KeyCode.R, Rotate, allowRepeat: false),
        Bind(KeyCode.F5, ToggleSlideshow, allowRepeat: false),
        Bind(KeyCode.Equals, () => Zoom(1.25f)),
        Bind(KeyCode.NumPadAdd, () => Zoom(1.25f)),
        Bind(KeyCode.Minus, () => Zoom(0.8f)),
        Bind(KeyCode.NumPadSubtract, () => Zoom(0.8f)),
        Bind(KeyCode.D0, ResetZoom),
        Bind(KeyCode.NumPad0, ResetZoom),
        Bind(KeyCode.Escape, HandleEscape, allowRepeat: false),
    ];

    private static KeyBinding Bind(KeyCode key, Action handler, ModifierKeys modifiers = ModifierKeys.None, bool allowRepeat = true) =>
        new(new Hotkey(modifiers, key), handler, AllowRepeat: allowRepeat);

    private Node Welcome()
    {
        Node backdrop = wallpaper is null
            ? Node.Empty
            : new Image(wallpaper).Fit(ImageFit.Cover).Expand();
        return new Stack(
        [
            backdrop,
            new Column(
                crossAxisAlignment: CrossAxisAlignment.Center,
                spacing: 14,
                children:
                [
                    new Spacer(),
                    new Row(
                        spacing: 10,
                        mainAxisAlignment: MainAxisAlignment.Center,
                        children:
                        [
                            new Button("Open file", () => _ = OpenWithPickerAsync()).Height(36),
                            new Button("Open folder", () => _ = OpenFolderWithPickerAsync()).Variant("ghost").Height(36),
                        ]),
                    new Label("or drop photos, videos or a folder here")
                        .FontSize(13).Color(TextSecondary),
                ]).Padding(EdgeInsets.Only(bottom: 110)),
        ]).WindowDragArea();
    }

    private Node Viewer()
    {
        var layers = new List<Node> { stage.Build(Invalidate, OnPointerActivity).OnContextMenu(ShowPictureMenu) };
        if (loading)
        {
            layers.Add(new Center(new Spinner(28f)).PassThroughHits());
        }
        else if (openError is not null)
        {
            layers.Add(OpenErrorView(openError).PassThroughHits());
        }
        return new Stack([.. layers]);
    }

    private Node OpenErrorView(string reason) =>
        new Center(
            new Column(
                spacing: 10,
                crossAxisAlignment: CrossAxisAlignment.Center,
                children:
                [
                    new IconView(LucideIcons.ImageOff, size: 44).Color(new ColorValue("#5A5A5F")),
                    new Label(Path.GetFileName(folder?.CurrentFile ?? "")).FontSize(15).Color(TextPrimary)
                        .MaxLines(1).Overflow(TextOverflow.Ellipsis),
                    new Label(reason).FontSize(13).Color(TextSecondary),
                ]));

    private Node SideArrows()
    {
        if (folder is null || folder.Count < 2)
        {
            return Node.Empty;
        }
        return new Row(
            crossAxisAlignment: CrossAxisAlignment.Center,
            children:
            [
                RoundButton(LucideIcons.ChevronLeft, () => Navigate(-1), "Previous (←)"),
                new Spacer(),
                RoundButton(LucideIcons.ChevronRight, () => Navigate(1), "Next (→)"),
            ]).Padding(EdgeInsets.Symmetric(horizontal: 20, vertical: 0));
    }

    private static Node RoundButton(Icon icon, Action onClick, string tooltip) =>
        new IconButton(icon, onClick)
            .Size(44).IconSize(22)
            .Tooltip(tooltip)
            .AccessibleLabel(icon.AccessibleName)
            .Background(ToolbarBackground)
            .CornerRadius(22)
            .Border(ToolbarHairline, 1, 22);

    private Node ToolbarLayer() =>
        new Column(
            crossAxisAlignment: CrossAxisAlignment.Center,
            children: [new Spacer(), Toolbar()])
            .Padding(EdgeInsets.Only(bottom: 22));

    private Node Toolbar()
    {
        bool hasFile = folder?.CurrentFile is not null;
        bool canNavigate = folder is not null && folder.Count > 1;
        bool hasPicture = stage.HasPicture;

        var buttons = new List<Node>();
        if (hasFile)
        {
            buttons.Add(ToolButton(LucideIcons.ChevronLeft, () => Navigate(-1), "Previous (←)", !canNavigate));
            buttons.Add(ToolButton(slideshowRunning ? LucideIcons.Pause : LucideIcons.Slideshow, ToggleSlideshow,
                slideshowRunning ? "Stop slideshow (F5)" : "Slideshow (F5)", !canNavigate, highlighted: slideshowRunning));
            buttons.Add(ToolButton(LucideIcons.ChevronRight, () => Navigate(1), "Next (→)", !canNavigate));
            buttons.Add(ToolbarDivider());
            buttons.Add(ToolButton(LucideIcons.RotateClockwise, Rotate, "Rotate (R)", !hasPicture));
            buttons.Add(ToolButton(LucideIcons.Copy, () => _ = CopyCurrentAsync(), "Copy (Ctrl+C)", false));
            buttons.Add(ToolButton(LucideIcons.Trash, () => _ = DeleteCurrentAsync(), "Delete (Del)", false));
            buttons.Add(ToolbarDivider());
        }
        bool fullScreen = App.Window.IsFullScreen;
        buttons.Add(ToolButton(fullScreen ? LucideIcons.Minimize : LucideIcons.Maximize, ToggleFullScreen,
            fullScreen ? "Exit full screen (Esc)" : "Full screen (F11)", false));
        buttons.Add(ToolButton(LucideIcons.FolderOpen, ShowOpenMenu, "Open (Ctrl+O, Ctrl+Shift+O)", false));
        buttons.Add(ToolButton(LucideIcons.Settings, () => _ = OpenSettingsAsync(), "Settings (Ctrl+,)", false));

        return new Row(spacing: 2, crossAxisAlignment: CrossAxisAlignment.Center, children: [.. buttons])
            .Padding(EdgeInsets.Symmetric(horizontal: 8, vertical: 6))
            .Background(ToolbarBackground)
            .CornerRadius(14)
            .Border(ToolbarHairline, 1, 14)
            .OnPointerMove(_ => OnPointerActivity());
    }

    private static Node ToolButton(Icon icon, Action onClick, string tooltip, bool disabled, bool highlighted = false)
    {
        var button = new IconButton(icon, onClick)
            .Variant("ghost")
            .Size(36).IconSize(19)
            .Disabled(disabled)
            .Tooltip(tooltip)
            .AccessibleLabel(icon.AccessibleName);
        return highlighted ? button.Background(Accent).CornerRadius(9) : button;
    }

    private static Node ToolbarDivider() =>
        new Column(children: []).Width(1).Height(20).Background(Divider).Margin(EdgeInsets.Symmetric(horizontal: 6, vertical: 0));

    private Node NoticeLayer()
    {
        var children = new List<Node>
        {
            new Label(notice!).FontSize(13).Color(TextPrimary).MaxLines(1).Overflow(TextOverflow.Ellipsis),
        };
        if (noticeOffersUndo)
        {
            children.Add(new Button("Undo", () => _ = UndoDeleteAsync(), LucideIcons.Undo).Variant("ghost").Height(28));
        }

        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Center,
            children:
            [
                new Row(spacing: 12, crossAxisAlignment: CrossAxisAlignment.Center, children: [.. children])
                    .Padding(EdgeInsets.Symmetric(horizontal: 16, vertical: 8))
                    .Background(ToolbarBackground)
                    .CornerRadius(12)
                    .Border(ToolbarHairline, 1, 12),
                new Spacer(),
            ]).Padding(EdgeInsets.Only(top: 18));
    }
}
