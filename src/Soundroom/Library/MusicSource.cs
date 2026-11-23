using System.Runtime.InteropServices;
using System.Text;

namespace Soundroom;

internal sealed record MusicSource(string Id, string Name, string? Volume, string Folder)
{
    public static MusicSource Register(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var root = Path.GetPathRoot(folder)!;
        var volume = VolumeAt(root);
        string relative = volume is null ? folder : Path.GetRelativePath(root, folder);
        var label = new DriveInfo(root);
        string name = Path.GetFileName(folder);
        if (name.Length == 0)
            name = label.IsReady && label.VolumeLabel.Length > 0 ? label.VolumeLabel : root;
        return new(Guid.NewGuid().ToString("N"), name, volume, relative);
    }

    public string? Resolve(IReadOnlyDictionary<string, string> volumes)
    {
        string? location = Volume is null ? Folder :
            volumes.TryGetValue(Volume, out var root) ? Path.GetFullPath(Path.Combine(root, Folder)) : null;
        return location is not null && Directory.Exists(location) ? location : null;
    }

    public static Dictionary<string, string> MountedVolumes()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in Directory.GetLogicalDrives())
            if (VolumeAt(root) is { } volume)
                result.TryAdd(volume, root);
        return result;
    }

    private static string? VolumeAt(string root)
    {
        var name = new StringBuilder(128);
        return GetVolumeNameForVolumeMountPoint(root, name, (uint)name.Capacity) ? name.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder name, uint capacity);
}

internal sealed record FileStamp(long Length, long ModifiedUtc);

internal static class SourceScanner
{
    public static Dictionary<string, FileStamp> Snapshot(string root, List<string>? inaccessible = null,
        Action? waitForPlayback = null)
    {
        var result = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new(root));
        while (pending.TryPop(out var folder))
        {
            waitForPlayback?.Invoke();
            try
            {
                int entries = 0;
                foreach (var entry in folder.EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint
                }))
                {
                    if (++entries % 64 == 0)
                        waitForPlayback?.Invoke();
                    if (entry is DirectoryInfo directory)
                        pending.Push(directory);
                    else if (entry is FileInfo file && !file.Extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) &&
                             Media.Extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                        result[Path.GetRelativePath(root, file.FullName)] = new(file.Length, file.LastWriteTimeUtc.Ticks);
                }
            }
            catch (UnauthorizedAccessException) when (!string.Equals(folder.FullName, root, StringComparison.OrdinalIgnoreCase))
            {
                inaccessible?.Add(Path.GetRelativePath(root, folder.FullName) + Path.DirectorySeparatorChar);
            }
        }
        return result;
    }

    internal static async Task WaitForPlaybackAsync(Func<bool> needsPlaybackBuffer, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        while (needsPlaybackBuffer())
            await Task.Delay(30, cancellation).ConfigureAwait(false);
    }
}
