using System.Text.Json;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Soundroom;

public sealed partial class MainWindow
{
    private readonly ArtworkCache thumbnails;
    private sealed record BookletAsset(string Name, string Path, int EmbeddedIndex = -1)
    {
        public bool Pdf => EmbeddedIndex < 0 && System.IO.Path.GetExtension(Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }
    private string? bookletKey;
    private BookletAsset[] bookletAssets = [];

    private static IEnumerable<string> FindArtwork(string folder) => !Directory.Exists(folder) ? [] :
        Directory.EnumerateFiles(folder).Where(item => new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" }
            .Contains(Path.GetExtension(item), StringComparer.OrdinalIgnoreCase))
        .OrderBy(item => Path.GetFileNameWithoutExtension(item).ToLowerInvariant() switch
        {
            "cover" or "folder" or "front" => 0, "back" or "disc" or "artist" or "booklet" => 1, _ => 2
        });

    private async Task<byte[]> ReadCoverBytesAsync(Track selected, CancellationToken cancellation)
    {
        var embedded = await thumbnails.GetAsync(selected.Path, embedded: true, cancellation).ConfigureAwait(false);
        if (embedded.Length > 0)
            return embedded;
        return FindArtwork(selected.Folder).FirstOrDefault() is { } artwork
            ? await thumbnails.GetAsync(artwork, embedded: false, cancellation).ConfigureAwait(false) : [];
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
