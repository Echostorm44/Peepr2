using Cascade.UI;
using MediaKernel.Playback;

namespace Peepr;

/// <summary>
/// One video playing through MediaKernel. The player decodes, keeps sound and picture in step and converts
/// frames on its own threads; the UI thread calls <see cref="PumpFrame"/> once per display frame to take the
/// newest picture, which is written into a single <see cref="ImageSource"/> whose texture is refreshed in
/// place — nothing is allocated per frame.
/// </summary>
internal sealed class VideoPlayback : IDisposable
{
    private readonly MediaPlayer player;
    private readonly MediaPlayer.PictureHandler takePicture;
    private ImageSource? picture;
    private bool awaitingScrubPicture;
    private bool disposed;

    private VideoPlayback(MediaPlayer player)
    {
        this.player = player;
        takePicture = TakePicture;
    }

    /// <summary>Opens and starts playing <paramref name="path"/>. Opening parses the file, so it runs off the UI thread.</summary>
    public static Task<VideoPlayback> OpenAsync(string path, float volume) =>
        Task.Run(() => new VideoPlayback(MediaPlayer.Open(path, new MediaPlayerOptions { Volume = volume })));

    public string FilePath => player.FilePath;

    /// <summary>The newest picture, or null until the first one arrives (or for a file with no video track).</summary>
    public ImageSource? Picture => picture;

    public bool HasVideo => player.HasVideo;

    /// <summary>The video's own size (frames handed over may be scaled down to the display).</summary>
    public int VideoWidth => player.VideoWidth > 0 ? player.VideoWidth : picture?.Width ?? 0;

    public int VideoHeight => player.VideoHeight > 0 ? player.VideoHeight : picture?.Height ?? 0;

    public TimeSpan Position => player.Position;

    public TimeSpan Duration => player.Duration;

    public bool IsPaused => player.IsPaused;

    public bool Ended => player.Ended;

    public Exception? Fault => player.Fault;

    /// <summary>True while frames are still coming: playing, not paused, not finished, not failed.</summary>
    public bool IsRunning => !disposed && !player.IsPaused && !player.Ended && player.Fault is null;

    /// <summary>
    /// True while the display should keep asking for pictures: while playing, after a seek while paused until the
    /// picture at the new position has arrived, and when a repeating video has just ended. The player's own thread
    /// marks the end at any moment, so <see cref="PumpFrame"/> must still run to start it again.
    /// </summary>
    public bool NeedsFrames => IsRunning || (!disposed && (awaitingScrubPicture || (Repeat && player.Ended && player.Fault is null)));

    /// <summary>Start again from the beginning on reaching the end, instead of stopping there.</summary>
    public bool Repeat { get; set; }

    public float Volume
    {
        get => player.Volume;
        set => player.Volume = value;
    }

    /// <summary>
    /// Advances the clock and takes the picture that is due, if any. UI thread, once per display frame.
    /// Returns true when the first picture has just arrived.
    /// </summary>
    public bool PumpFrame()
    {
        if (disposed)
        {
            return false;
        }

        bool hadPicture = picture is not null;
        player.Tick();
        if (Repeat && player.Ended && player.Fault is null)
        {
            player.TogglePause(); // a finished player plays again from the start
        }
        if (player.TryTakePicture(takePicture))
        {
            awaitingScrubPicture = false;
        }
        return !hadPicture && picture is not null;
    }

    private void TakePicture(ReadOnlySpan<byte> pixels, int width, int height, long presentationTimeTicks)
    {
        if (picture is null)
        {
            picture = ImageSource.FromPixels(pixels.ToArray(), width, height);
            return;
        }
        picture.Update(pixels, width, height);
    }

    /// <summary>The device-pixel box the picture is drawn in, so larger frames are scaled down before they are handed over.</summary>
    public void SetDisplaySize(int width, int height) => player.SetDisplaySize(width, height);

    /// <summary>Pauses, resumes, or (once finished) plays again from the start.</summary>
    public void TogglePause() => player.TogglePause();

    public void Pause() => player.Pause();

    /// <summary>Moves playback to <paramref name="position"/>. A paused video stays paused and shows the picture there.</summary>
    public void SeekTo(TimeSpan position)
    {
        player.SeekTo(position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position);
        awaitingScrubPicture = player.IsPaused;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        player.Dispose();
        picture?.Dispose();
        picture = null;
    }
}
