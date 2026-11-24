using NAudio.Wave;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Soundroom;

public sealed partial class MainWindow
{
    private SystemMediaTransportControls? mediaControls;
    private string? mediaTrackKey;
    private int mediaTrackRevision;
    private MediaPlaybackStatus mediaStatus = MediaPlaybackStatus.Closed;
    private long mediaTimelineAt;

    private void InitializeMediaControls()
    {
        try
        {
            var controls = SystemMediaTransportControlsInterop.GetForWindow(WindowNative.GetWindowHandle(this));
            controls.IsEnabled = false;
            controls.IsPlayEnabled = true;
            controls.IsPauseEnabled = true;
            controls.IsNextEnabled = true;
            controls.IsPreviousEnabled = true;
            controls.ButtonPressed += MediaButtonPressed;
            controls.PlaybackPositionChangeRequested += MediaPositionRequested;
            mediaControls = controls;
        }
        catch (Exception error) { Diagnostics.Write("Opening Windows media controls", error); }
    }

    private void CloseMediaControls()
    {
        if (mediaControls is not { } controls)
            return;
        mediaControls = null;
        mediaTrackRevision++;
        Diagnostics.Cleanup("Closing Windows media controls", () =>
        {
            controls.ButtonPressed -= MediaButtonPressed;
            controls.PlaybackPositionChangeRequested -= MediaPositionRequested;
            controls.IsEnabled = false;
        });
    }

    private void MediaButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var button = args.Button;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!ready || !ReferenceEquals(mediaControls, sender))
                return;
            await RunUiAction("Handling a Windows media command", async () =>
            {
                switch (button)
                {
                    case SystemMediaTransportControlsButton.Play: await ResumePlaybackAsync(); break;
                    case SystemMediaTransportControlsButton.Pause: PausePlayback(); break;
                    case SystemMediaTransportControlsButton.Next: await MoveAsync(1); break;
                    case SystemMediaTransportControlsButton.Previous: await MoveAsync(-1); break;
                }
            });
            PublishWebState(force: true);
        });
    }

    private void MediaPositionRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
    {
        string? key = Volatile.Read(ref mediaTrackKey);
        double position = args.RequestedPlaybackPosition.TotalSeconds;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!ready || key is null || !ReferenceEquals(mediaControls, sender))
                return;
            await RunUiAction("Seeking from Windows media controls", () => SeekAsync(key, position));
            PublishWebState(force: true);
        });
    }

    private void UpdateMediaControls(bool force)
    {
        if (!ready || mediaControls is not { } controls)
            return;
        try
        {
            var selected = track;
            bool changed = mediaTrackKey != selected?.Key;
            if (changed)
            {
                Volatile.Write(ref mediaTrackKey, selected?.Key);
                int revision = ++mediaTrackRevision;
                var display = controls.DisplayUpdater;
                display.ClearAll();
                if (selected is not null)
                {
                    display.Type = MediaPlaybackType.Music;
                    display.MusicProperties.Title = selected.Title;
                    display.MusicProperties.Artist = selected.Artist;
                    display.MusicProperties.AlbumArtist = selected.Tag("albumartist", "album_artist");
                    display.MusicProperties.AlbumTitle = selected.Album;
                }
                display.Update();
                controls.IsEnabled = selected is not null;
                if (selected is not null)
                    _ = UpdateMediaThumbnailAsync(controls, selected, revision);
            }
            MediaPlaybackStatus state = selected is null ? MediaPlaybackStatus.Closed : playback?.State switch
            {
                PlaybackState.Playing => MediaPlaybackStatus.Playing,
                PlaybackState.Paused => MediaPlaybackStatus.Paused,
                _ when playbackRequested && preparingPlayback is not null => MediaPlaybackStatus.Changing,
                _ when playback is not null => MediaPlaybackStatus.Paused,
                _ => MediaPlaybackStatus.Stopped
            };
            bool stateChanged = mediaStatus != state;
            if (stateChanged)
            {
                controls.PlaybackStatus = state;
                mediaStatus = state;
            }
            long now = Environment.TickCount64;
            if (selected is null || !(force || changed || stateChanged ||
                (state == MediaPlaybackStatus.Playing && now - mediaTimelineAt >= 1000)))
                return;
            var duration = TimeSpan.FromSeconds(selected.Duration);
            controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                EndTime = duration,
                MaxSeekTime = duration,
                Position = TimeSpan.FromSeconds(Math.Clamp(WebPosition(), 0, selected.Duration))
            });
            mediaTimelineAt = now;
        }
        catch (Exception error)
        {
            Diagnostics.Write("Updating Windows media controls", error);
            CloseMediaControls();
        }
    }

    private async Task UpdateMediaThumbnailAsync(SystemMediaTransportControls controls, Track selected, int revision)
    {
        try
        {
            var cancellation = libraryLifetime.Token;
            var available = ResolvePlaybackTrack(selected);
            var bytes = await Task.Run(async () =>
            {
                var cover = await ReadCoverBytesAsync(available, cancellation).ConfigureAwait(false);
                return ArtworkCache.IsWebP(cover) ? await EncodeMediaThumbnailAsync(cover).ConfigureAwait(false) : cover;
            }, cancellation);
            if (!ready || mediaTrackRevision != revision || bytes.Length == 0)
                return;
            var file = await StorageFile.CreateStreamedFileAsync("cover.png", request => WriteMediaThumbnail(request, bytes), null);
            if (!ready || mediaTrackRevision != revision)
                return;
            var display = controls.DisplayUpdater;
            display.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
            display.Update();
        }
        catch (OperationCanceledException) when (libraryLifetime.IsCancellationRequested) { }
        catch (Exception error) { Diagnostics.Write("Updating Windows media artwork", error); }
    }

    private static async Task<byte[]> EncodeMediaThumbnailAsync(byte[] cover)
    {
        using var input = await ImageStream(cover);
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var image = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(image);
        await encoder.FlushAsync();
        output.Seek(0);
        using var source = output.AsStreamForRead();
        using var result = new MemoryStream();
        await source.CopyToAsync(result).ConfigureAwait(false);
        return result.ToArray();
    }

    private static async void WriteMediaThumbnail(StreamedFileDataRequest request, byte[] bytes)
    {
        try
        {
            using (var stream = request.AsStreamForWrite())
                await stream.WriteAsync(bytes);
            request.Dispose();
        }
        catch (Exception error)
        {
            Diagnostics.Write("Streaming Windows media artwork", error);
            request.FailAndClose(StreamedFileFailureMode.Incomplete);
        }
    }
}
