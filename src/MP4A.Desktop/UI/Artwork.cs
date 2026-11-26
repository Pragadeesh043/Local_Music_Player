using System.Collections.Concurrent;
using System.Text.Json;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Soundroom;

public sealed partial class MainWindow
{
    private readonly ArtworkCache thumbnails;
    private readonly ConcurrentDictionary<string, Lazy<Task<(StoredCover? Cover, byte[] Bytes)>>> coverJobs =
        new(StringComparer.OrdinalIgnoreCase);
    private sealed record BookletAsset(string Name, string Path, int EmbeddedIndex = -1)
    {
        public bool Pdf => EmbeddedIndex < 0 && System.IO.Path.GetExtension(Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }
    private string? bookletKey;
    private BookletAsset[] bookletAssets = [];

    private static IEnumerable<string> FindArtwork(string folder) => !Directory.Exists(folder) ? [] :
        Directory.EnumerateFiles(folder).Where(SourceScanner.IsArtwork)
        .OrderBy(item => Path.GetFileNameWithoutExtension(item).ToLowerInvariant() switch
        {
            "cover" or "folder" or "front" => 0, "back" or "disc" or "artist" or "booklet" => 1, _ => 2
        }).ThenBy(item => item, StringComparer.OrdinalIgnoreCase);

    private async Task<(byte[] Bytes, string Path, bool Embedded)> ReadCoverSourceAsync(Track selected, CancellationToken cancellation)
    {
        var embedded = await thumbnails.GetAsync(selected.Path, embedded: true, cancellation).ConfigureAwait(false);
        if (embedded.Length > 0)
            return (embedded, selected.Path, true);
        return FindArtwork(selected.Folder).FirstOrDefault() is { } artwork
            ? (await thumbnails.GetAsync(artwork, embedded: false, cancellation).ConfigureAwait(false), artwork, false)
            : ([], selected.Path, true);
    }

    private async Task<(StoredCover? Cover, byte[] Bytes)> PrepareCoverAsync(Track item, bool refresh = false)
    {
        var requested = new Lazy<Task<(StoredCover? Cover, byte[] Bytes)>>(() => Task.Run<(StoredCover? Cover, byte[] Bytes)>(async () =>
        {
            try
            {
                var cancellation = libraryLifetime.Token;
                var existing = thumbnails.ReadCovers([item.Key]).GetValueOrDefault(item.Key);
                if (existing is not null && (!refresh || !File.Exists(item.Path)))
                    return (existing, existing.Asset is { } id ? thumbnails.ReadCoverAsset(id) : []);
                if (!File.Exists(item.Path))
                    return (null, []);
                var (bytes, path, embedded) = await ReadCoverSourceAsync(item, cancellation).ConfigureAwait(false);
                if (bytes.Length == 0 && existing?.Asset is { } previous)
                    return (existing, thumbnails.ReadCoverAsset(previous));
                var cover = await thumbnails.StoreCoverAsync(item.Key, bytes,
                    image => EncodeCoverPreviewAsync(image, path, embedded, cancellation)).ConfigureAwait(false);
                return (cover, bytes);
            }
            finally { coverJobs.TryRemove(item.Key, out _); }
        }, CancellationToken.None));
        var job = coverJobs.GetOrAdd(item.Key, requested);
        var result = await job.Value.ConfigureAwait(false);
        return refresh && !ReferenceEquals(job, requested)
            ? await PrepareCoverAsync(item, refresh: true).ConfigureAwait(false) : result;
    }

    private async Task WarmCoverAsync(Track item, bool refresh = false)
    {
        try
        {
            await SourceScanner.WaitForPlaybackAsync(() => Volatile.Read(ref playback)?.NeedsScanBackpressure == true,
                libraryLifetime.Token);
            await PrepareCoverAsync(item, refresh);
        }
        catch (OperationCanceledException) when (libraryLifetime.IsCancellationRequested) { }
        catch (Exception error) { Diagnostics.Write("Indexing cover artwork", error); }
    }

    private async Task WarmLibraryArtworkAsync(bool refresh = false)
    {
        var cancellation = libraryLifetime.Token;
        var mounts = new Dictionary<string, string>(mountedSources, StringComparer.OrdinalIgnoreCase);
        const int batchSize = 100;
        await Task.Run(async () =>
        {
            var query = new LibraryQuery("", new SmartFilter());
            for (int offset = 0; !cancellation.IsCancellationRequested; offset += batchSize)
            {
                var page = library.Page(query, offset, batchSize);
                var stored = refresh ? null : thumbnails.ReadCovers(page.Tracks.Select(item => item.Key));
                foreach (var item in page.Tracks)
                {
                    if (cancellation.IsCancellationRequested)
                        return;
                    if (stored?.ContainsKey(item.Key) == true)
                        continue;
                    if (item.SourceId is null)
                        await WarmCoverAsync(item, refresh);
                    else if (item.RelativePath is { } path && mounts.TryGetValue(item.SourceId, out var root))
                        await WarmCoverAsync(item with { Path = Path.Combine(root, path) }, refresh);
                }
                if (offset + batchSize >= page.Total)
                    break;
            }
        }, cancellation);
    }

    private static async Task<string> EncodeCoverPreviewAsync(byte[] bytes, string path, bool embedded, CancellationToken cancellation)
    {
        const int size = 96;
        try
        {
            using var input = await ImageStream(bytes) ?? throw new InvalidOperationException("The cover is empty.");
            var decoder = await BitmapDecoder.CreateAsync(input);
            double scale = Math.Min(1, (double)size / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            output.Seek(0);
            using var stream = output.AsStreamForRead();
            using var preview = new MemoryStream();
            await stream.CopyToAsync(preview).ConfigureAwait(false);
            return "data:image/jpeg;base64," + Convert.ToBase64String(preview.ToArray());
        }
        catch (Exception error)
        {
            Diagnostics.Write("Encoding a cover preview", error);
            var (preview, _) = await Media.ThumbnailAsync(path, embedded, cancellation, size).ConfigureAwait(false);
            if (preview.Length == 0 && bytes.Length <= 24 * 1024)
                preview = bytes;
            return preview.Length == 0 ? "" :
                $"data:image/{(ArtworkCache.IsWebP(preview) ? "webp" : "png")};base64,{Convert.ToBase64String(preview)}";
        }
    }

    private async Task<object> ReadBooklet(string id)
    {
        var selected = ResolvePlaybackTrack(await FindWebTrackAsync(id));
        var embedded = await Media.ArtworkNamesAsync(selected.Path);
        var files = await Task.Run(() => FindArtwork(selected.Folder).Concat(Directory.Exists(selected.Folder)
            ? Directory.EnumerateFiles(selected.Folder, "*.pdf") : []).ToArray(), libraryLifetime.Token);
        bookletKey = id;
        bookletAssets = embedded.Select((name, i) => new BookletAsset(name, selected.Path, i))
            .Concat(files.Select(path => new BookletAsset(Path.GetFileName(path), path))).ToArray();
        return bookletAssets.Select(asset => new { asset.Name, asset.Pdf }).ToArray();
    }

    private BookletAsset GetBookletAsset(string id, int index) =>
        bookletKey == id && index >= 0 && index < bookletAssets.Length ? bookletAssets[index] :
            throw new InvalidOperationException("The artwork selection changed.");

    private async Task<object> ReadBookletPage(JsonElement args)
    {
        string id = WebText(args, "id");
        int index = args.GetProperty("index").GetInt32();
        var asset = GetBookletAsset(id, index);
        int pages = 1;
        if (asset.Pdf)
        {
            var file = await StorageFile.GetFileFromPathAsync(asset.Path);
            var pdf = await PdfDocument.LoadFromFileAsync(file);
            pages = checked((int)pdf.PageCount);
        }
        int page = Math.Clamp(args.GetProperty("page").GetInt32(), 0, Math.Max(0, pages - 1));
        return new { url = $"{MediaOrigin}booklet/{EncodeWebKey(id)}/{index}/{page}", pages, page };
    }

    private async Task<(IRandomAccessStream? Stream, string Type)> ServeBooklet(string path)
    {
        string[] request = path.Split('/');
        if (request.Length != 4 || !int.TryParse(request[2], out int index) ||
            !uint.TryParse(request[3], out uint page))
            return (null, "");
        var asset = GetBookletAsset(DecodeWebKey(request[1]), index);
        if (asset.EmbeddedIndex >= 0)
            return (await ImageStream(await Media.CoverAsync(asset.Path, asset.EmbeddedIndex)), "image/png");
        var file = await StorageFile.GetFileFromPathAsync(asset.Path);
        if (!asset.Pdf)
            return (await file.OpenReadAsync(), file.ContentType);
        var pdf = await PdfDocument.LoadFromFileAsync(file);
        if (page >= pdf.PageCount)
            return (null, "");
        using var documentPage = pdf.GetPage(page);
        var stream = new InMemoryRandomAccessStream();
        await documentPage.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = 1000 });
        stream.Seek(0);
        return (stream, "image/png");
    }

    private static async Task<InMemoryRandomAccessStream?> ImageStream(byte[] bytes)
    {
        if (bytes.Length == 0)
            return null;
        var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        writer.DetachStream();
        stream.Seek(0);
        return stream;
    }
}
