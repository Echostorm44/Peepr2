using Cascade.UI;
using Updater = Cascade.UI.Installer.Update.Updater;
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

    private const float ThumbnailTileSize = 56f;
    private const float ThumbnailGap = 6f;

    private readonly PictureCache pictureCache = new();
    private readonly ThumbnailCache thumbnails = new();
    private readonly PictureStage stage = new();
    private bool showThumbnails = PeeprSettings.ShowThumbnailStrip;
    // Decoded only when the welcome screen is shown, off the UI thread: opening a photo never pays for it.
    private ImageSource? wallpaper;
    private bool wallpaperRequested;
    private readonly Stack<DeletedFile> deletedFiles = new();

    private MediaFolder? folder;
    private VideoPlayback? video;
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

    private string? availableUpdate;
    private string? updateStatus;

    protected override Task OnMounted()
    {
        WindowPlacement.Restore();
        App.Window.OnCloseRequested = () =>
        {
            WindowPlacement.Save();
            return CloseResult.Propagate;
        };

        FileIntake.FilesReceived += OpenPaths;
        thumbnails.ThumbnailReady += Invalidate;
        IReadOnlyList<string> launchFiles = FileIntake.TakePending();
        if (launchFiles.Count > 0)
        {
            OpenPaths(launchFiles);
        }
        else
        {
            EnsureWallpaper();
        }

        if (Updater.IsConfigured)
        {
            // This launch reached a healthy state: defuse the update shim's crash rollback for this version.
            Updater.MarkHealthy();
            _ = CheckForUpdateAsync();
        }
        return Task.CompletedTask;
    }

    // ── Updates ───────────────────────────────────────────────────────

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var result = await Task.Run(() => Updater.CheckNowAsync());
            if (result.IsAvailable && result.Version is { Length: > 0 })
            {
                availableUpdate = result.Version;
                Invalidate();
            }
        }
        catch
        {
            // A failed check is invisible: no network, no release yet.
        }
    }

    private async Task InstallUpdateAsync()
    {
        updateStatus = "Downloading the update…";
        Invalidate();
        try
        {
            await Task.Run(() => Updater.DownloadAsync());
            updateStatus = "Restarting Peepr…";
            Invalidate();
            StopVideo();
            // Hands over to the update shim, which waits for Peepr to exit, swaps the files and relaunches.
            Updater.ApplyAndRestart();
        }
        catch
        {
            updateStatus = null;
            ShowNotice("The update didn't download. Peepr will offer it again later.", offerUndo: false);
            availableUpdate = null;
            Invalidate();
        }
    }

    private Node UpdateBanner() =>
        new Column(
            crossAxisAlignment: CrossAxisAlignment.End,
            children:
            [
                new Row(
                    spacing: 12,
                    crossAxisAlignment: CrossAxisAlignment.Center,
                    children: updateStatus is not null
                        ? [new Label(updateStatus).FontSize(13).Color(TextPrimary)]
                        :
                        [
                            new Label($"Peepr {availableUpdate} is available").FontSize(13).Color(TextPrimary),
                            new Button("Later", () => { availableUpdate = null; Invalidate(); }).Variant("ghost").Height(28),
                            new Button("Update", () => _ = InstallUpdateAsync()).Height(28),
                        ])
                    .Padding(EdgeInsets.Symmetric(horizontal: 14, vertical: 8))
                    .Background(ToolbarBackground)
                    .CornerRadius(12)
                    .Border(ToolbarHairline, 1, 12),
            ]).Padding(EdgeInsets.Only(top: 18, right: 18));

    protected override void OnUnmounted()
    {
        FileIntake.FilesReceived -= OpenPaths;
        thumbnails.ThumbnailReady -= Invalidate;
        StopVideo();
        stage.Clear();
        wallpaper?.Dispose();
    }

    // Called where the welcome screen is about to show (nothing open at start, or a folder emptied by deleting).
    private void EnsureWallpaper()
    {
        if (!wallpaperRequested)
        {
            wallpaperRequested = true;
            _ = LoadWallpaperAsync();
        }
    }

    private async Task LoadWallpaperAsync()
    {
        wallpaper = await Task.Run(() =>
        {
            using Stream? stream = typeof(MainView).Assembly.GetManifestResourceStream("PeeprBackDrop.png");
            return stream is null ? null : ImageSource.FromStream(stream);
        });
        Invalidate();
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
        StopVideo();
        if (file is null)
        {
            EnsureWallpaper();
            stage.Clear();
            loading = false;
            Invalidate();
            return;
        }

        if (MediaTypes.KindOf(file) == MediaKind.Video)
        {
            await PlayVideoAsync(file, generation);
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

    private async Task PlayVideoAsync(string file, int generation)
    {
        stage.Clear();
        loading = true;
        Invalidate();

        VideoPlayback playback;
        try
        {
            playback = await VideoPlayback.OpenAsync(file, PeeprSettings.Muted ? 0f : PeeprSettings.Volume);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation == showGeneration)
            {
                loading = false;
                openError = DescribeOpenFailure(ex);
                Invalidate();
            }
            return;
        }

        if (generation != showGeneration)
        {
            // The user moved on while the file was opening.
            playback.Dispose();
            return;
        }

        video = playback;
        stage.ShowVideo(playback);
        // The spinner stays until the first frame arrives (OnVideoProgress); a file with only sound has none.
        loading = playback.HasVideo;
        if (!playback.HasVideo)
        {
            openError = "This file has sound but no picture.";
        }
        Invalidate();
    }

    // The player reports a first frame, a quarter second of progress, or that it stopped: refresh the controls.
    private void OnVideoProgress()
    {
        if (video is null)
        {
            return;
        }
        if (loading && video.Picture is not null)
        {
            loading = false;
        }
        if (video.Fault is not null)
        {
            openError = "This video stopped playing: part of it couldn't be decoded.";
        }
        Invalidate();
    }

    private void StopVideo()
    {
        if (video is null)
        {
            return;
        }
        stage.Clear();
        video.Dispose();
        video = null;
        MemoryRelief.AfterVideoClosed();
    }

    private void TogglePlayPause()
    {
        if (video is null)
        {
            return;
        }
        video.TogglePause();
        Invalidate();
    }

    private void SeekBy(TimeSpan offset)
    {
        if (video is null)
        {
            return;
        }
        video.SeekTo(video.Position + offset);
        Invalidate();
    }

    private void ToggleMute()
    {
        PeeprSettings.Muted = !PeeprSettings.Muted;
        if (video is not null)
        {
            video.Volume = PeeprSettings.Muted ? 0f : PeeprSettings.Volume;
        }
        Invalidate();
    }

    private void SetVolume(float volume)
    {
        PeeprSettings.Volume = volume;
        PeeprSettings.Muted = volume <= 0f;
        if (video is not null)
        {
            video.Volume = volume;
        }
        Invalidate();
    }

    // Space plays and pauses a video, and steps to the next file otherwise.
    private void HandleSpace()
    {
        if (video is not null)
        {
            TogglePlayPause();
            return;
        }
        Navigate(1);
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
            ContextMenuItem.Action("Reset zoom", ResetZoom, shortcut: "Z", disabled: !stage.IsZoomed),
            ContextMenuItem.Separator(),
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

        // A playing video holds its file open; let go of it before Windows moves it.
        bool wasPlaying = video is not null && string.Equals(video.FilePath, file, StringComparison.OrdinalIgnoreCase);
        if (wasPlaying)
        {
            StopVideo();
        }

        bool recycled = await Task.Run(() => RecycleBin.Send(file));
        if (!recycled)
        {
            ShowNotice($"Couldn't delete {Path.GetFileName(file)}. Is it open somewhere else?", offerUndo: false);
            if (wasPlaying)
            {
                await ShowCurrentAsync();
            }
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

    // Each photo stays for the chosen delay; a video plays to its end first (or for as long as it is paused).
    private async Task RunSlideshowAsync(int generation)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(PeeprSettings.SlideshowSeconds), LifetimeToken).ConfigureAwait(true);
            while (video is { Ended: false, Fault: null } && generation == slideshowGeneration)
            {
                await Task.Delay(250, LifetimeToken).ConfigureAwait(true);
            }
            if (generation != slideshowGeneration || !slideshowRunning)
            {
                return;
            }
            Navigate(1);
        }
    }

    private Task OpenSettingsAsync() => Dialog.ShowAsync(new SettingsView(OnSettingsChanged), new DialogOptions { Title = "Settings" });

    private void ToggleThumbnails()
    {
        showThumbnails = !showThumbnails;
        PeeprSettings.ShowThumbnailStrip = showThumbnails;
        Invalidate();
    }

    private void OnSettingsChanged()
    {
        showThumbnails = PeeprSettings.ShowThumbnailStrip;
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
        if (availableUpdate is not null)
        {
            layers.Add(UpdateBanner().PassThroughHits());
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
        Bind(KeyCode.Space, HandleSpace, allowRepeat: false),
        Bind(KeyCode.Left, () => SeekBy(TimeSpan.FromSeconds(-5)), ModifierKeys.Shift),
        Bind(KeyCode.Right, () => SeekBy(TimeSpan.FromSeconds(5)), ModifierKeys.Shift),
        Bind(KeyCode.M, ToggleMute, allowRepeat: false),
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
        Bind(KeyCode.Z, ResetZoom, allowRepeat: false),
        Bind(KeyCode.T, ToggleThumbnails, allowRepeat: false),
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
        var layers = new List<Node> { stage.Build(Invalidate, OnPointerActivity, OnVideoProgress, ResetZoom).OnContextMenu(ShowPictureMenu) };
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
            spacing: 10,
            crossAxisAlignment: CrossAxisAlignment.Center,
            children:
            [
                new Spacer(),
                video is { HasVideo: true } ? VideoControls(video) : Node.Empty,
                Toolbar(),
                showThumbnails && folder is { Count: > 1 } ? ThumbnailStrip(folder) : Node.Empty,
            ])
            .Padding(EdgeInsets.Only(bottom: 22));

    // The files around the current one, as many as fit across the window, following it as you move.
    private Node ThumbnailStrip(MediaFolder strip)
    {
        // The root component has no layout bounds of its own; the window's client area is what there is to fill.
        float available = MathF.Max(App.Window.ClientBounds.Width - 96f, ThumbnailTileSize);
        int fits = Math.Max(1, (int)((available + ThumbnailGap) / (ThumbnailTileSize + ThumbnailGap)));
        int visible = Math.Min(strip.Count, fits);
        int start = Math.Clamp(strip.CurrentIndex - visible / 2, 0, strip.Count - visible);

        // Nearest first, so the tiles beside the current file fill in before the far ones.
        thumbnails.Request(Enumerable.Range(start, visible)
            .OrderBy(i => Math.Abs(i - strip.CurrentIndex))
            .Select(i => strip.Files[i]));

        var tiles = new Node[visible];
        for (int i = 0; i < visible; i++)
        {
            tiles[i] = ThumbnailTile(strip.Files[start + i], start + i, start + i == strip.CurrentIndex);
        }

        return new Row(spacing: ThumbnailGap, crossAxisAlignment: CrossAxisAlignment.Center, children: tiles)
            .Padding(EdgeInsets.All(6))
            .Background(ToolbarBackground)
            .CornerRadius(14)
            .Border(ToolbarHairline, 1, 14)
            .OnPointerMove(_ => OnPointerActivity());
    }

    private Node ThumbnailTile(string path, int index, bool current)
    {
        Node content = thumbnails.Get(path) is { } image
            ? new Image(image).Fit(ImageFit.Cover).Size(ThumbnailTileSize, ThumbnailTileSize)
            : MediaTypes.KindOf(path) == MediaKind.Video
                ? new Center(new IconView(LucideIcons.Film, size: 22).Color(TextSecondary))
                : Node.Empty;

        // The ring is its own layer above the picture; a border on the tile itself is painted under it.
        Node ring = current
            ? new Column(children: []).Size(ThumbnailTileSize, ThumbnailTileSize).Border(Accent, 2.5f, 8)
            : Node.Empty;

        return new Stack([content, ring])
            .Size(ThumbnailTileSize, ThumbnailTileSize)
            .Background(new ColorValue("#1C1C1E"))
            .CornerRadius(8)
            .ClipToBounds(true)
            .Opacity(current ? 1f : 0.7f)
            .OnTap(() => NavigateTo(index))
            .AccessibleLabel(Path.GetFileName(path));
    }

    // Play/pause, elapsed time, a seek bar, time remaining, and sound: the QuickTime arrangement.
    private Node VideoControls(VideoPlayback playback)
    {
        double durationSeconds = Math.Max(playback.Duration.TotalSeconds, 0.001);
        float position = (float)Math.Clamp(playback.Position.TotalSeconds, 0, durationSeconds);
        bool muted = PeeprSettings.Muted;
        Icon playIcon = playback.Ended ? LucideIcons.Replay : playback.IsPaused ? LucideIcons.Play : LucideIcons.Pause;

        return new Row(
            spacing: 10,
            crossAxisAlignment: CrossAxisAlignment.Center,
            children:
            [
                ToolButton(playIcon, TogglePlayPause, playback.IsPaused || playback.Ended ? "Play (Space)" : "Pause (Space)", false),
                new Label(FormatTime(playback.Position)).FontSize(12).Color(TextSecondary).Width(52).TextAlign(TextAlignment.End),
                new Slider(
                    Bind(position, seconds => playback.SeekTo(TimeSpan.FromSeconds(seconds))),
                    0f, (float)durationSeconds)
                    .AccessibleLabel("Position")
                    .Expand(),
                new Label("-" + FormatTime(playback.Duration - playback.Position)).FontSize(12).Color(TextSecondary).Width(56),
                ToolButton(muted ? LucideIcons.VolumeMuted : LucideIcons.Volume, ToggleMute, muted ? "Unmute (M)" : "Mute (M)", false),
                new Slider(Bind(muted ? 0f : PeeprSettings.Volume, SetVolume), 0f, 1f)
                    .AccessibleLabel("Volume")
                    .Width(90),
            ])
            .Width(620)
            .Padding(EdgeInsets.Symmetric(horizontal: 10, vertical: 6))
            .Background(ToolbarBackground)
            .CornerRadius(14)
            .Border(ToolbarHairline, 1, 14)
            .OnPointerMove(_ => OnPointerActivity());
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

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
        if (hasFile)
        {
            buttons.Add(ToolButton(LucideIcons.Thumbnails, ToggleThumbnails, showThumbnails ? "Hide thumbnails (T)" : "Show thumbnails (T)", false, highlighted: showThumbnails));
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
