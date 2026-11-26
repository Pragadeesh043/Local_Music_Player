using System.Security.Cryptography;
using System.Text;

namespace Soundroom;

internal static class SacdImage
{
    public static async Task<Track[]> ImportAsync(string path, string? dataDirectory = null)
    {
        var file = new FileInfo(path);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")))[..24];
        string cache = Path.Combine(dataDirectory ?? Diagnostics.DataDirectory, "SACD", fingerprint);
        string packaged = Path.Combine(AppContext.BaseDirectory, "Tools", "SACD", "sacd_extract.exe");
        string extractor = File.Exists(packaged) ? packaged : "sacd_extract";
        var result = new List<Track>();
        IOException? extractionFailure = null;
        foreach (var (area, option) in new[] { ("Stereo", "-2"), ("Multichannel", "-m") })
        {
            string folder = Path.Combine(cache, area);
            string manifest = Path.Combine(folder, ".complete");
            string? cached = File.Exists(manifest) ? await File.ReadAllTextAsync(manifest) : null;
            bool extracting = !Directory.Exists(cached);
            if (extracting)
            {
                folder += "-" + Guid.NewGuid().ToString("N")[..8];
                Directory.CreateDirectory(folder);
                try
                {
                    using var tool = Media.Start(extractor, option, "-s", "-c", $"-i{file.FullName}", $"--output-dir={folder}");
                    var stdout = tool.StandardOutput.ReadToEndAsync();
                    var stderr = tool.StandardError.ReadToEndAsync();
                    await tool.WaitForExitAsync();
                    string output = await stdout;
                    string detail = await stderr;
                    if (tool.ExitCode != 0)
                    {
                        string? message = output.Split('\n').Select(line => line.Trim())
                            .FirstOrDefault(line => line.StartsWith("Error", StringComparison.OrdinalIgnoreCase));
                        extractionFailure = Media.ToolFailure("sacd_extract", tool.ExitCode,
                            string.IsNullOrWhiteSpace(detail) ? message ?? "" : detail);
                        Diagnostics.Write($"Extracting SACD {area} area", extractionFailure);
                        continue;
                    }
                }
                catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 })
                {
                    throw new IOException(
                        "SACD ISO import requires EuFlo sacd_extract. Put sacd_extract.exe in MP4A's Tools\\SACD folder or on PATH.", error);
                }
            }
            else
                folder = cached!;
            int areaStart = result.Count;
            foreach (string item in Directory.EnumerateFiles(folder, "*.dsf", SearchOption.AllDirectories).Order())
            {
                try
                {
                    var track = await Media.ProbeAsync(item);
                    using var reader = new DsdReader(track, 0, 24);
                    if (reader.Read(new byte[track.Channels * 3]) == 0)
                        throw new IOException("The extracted DSF contains no DSD frames.");
                    track.Tags["sacd_image"] = path;
                    track.Tags["edition"] = $"SACD {area}";
                    result.Add(track);
                }
                catch (Exception error) when (error is IOException or NotSupportedException)
                {
                    Diagnostics.Write($"Reading extracted SACD track {Path.GetFileName(item)}", error);
                }
            }
            if (extracting && result.Count > areaStart)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
                await File.WriteAllTextAsync(manifest, folder);
            }
        }
        if (result.Count == 0)
            throw new IOException("The SACD extractor found no playable areas in this image. Check the Soundroom log for details.", extractionFailure);
        return result.ToArray();
    }
}
