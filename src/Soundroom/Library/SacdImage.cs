using System.Security.Cryptography;
using System.Text;

namespace Soundroom;

internal static class SacdImage
{
    public static async Task<Track[]> ImportAsync(string path)
    {
        var file = new FileInfo(path);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")))[..24];
        string cache = Path.Combine(Diagnostics.DataDirectory, "SACD", fingerprint);
        string packaged = Path.Combine(AppContext.BaseDirectory, "Tools", "SACD", "sacd_extract.exe");
        string extractor = File.Exists(packaged) ? packaged : "sacd_extract";
        var result = new List<Track>();
        IOException? extractionFailure = null;
        foreach (var (area, option) in new[] { ("Stereo", "-2"), ("Multichannel", "-m") })
        {
            string folder = Path.Combine(cache, area);
            string manifest = Path.Combine(folder, ".complete");
            if (!File.Exists(manifest))
            {
                folder += "-" + Guid.NewGuid().ToString("N")[..8];
                Directory.CreateDirectory(folder);
                try
                {
                    using var tool = Media.Start(extractor, option, "-s", "-c", $"-i{file.FullName}", "-y", folder);
                    var stdout = tool.StandardOutput.ReadToEndAsync();
                    var stderr = tool.StandardError.ReadToEndAsync();
                    await tool.WaitForExitAsync();
                    _ = await stdout;
                    string detail = await stderr;
                    if (tool.ExitCode != 0)
                    {
                        extractionFailure = Media.ToolFailure("sacd_extract", tool.ExitCode, detail);
                        Diagnostics.Write($"Extracting SACD {area} area", extractionFailure);
                        continue;
                    }
                    if (!Directory.EnumerateFiles(folder, "*.dsf", SearchOption.AllDirectories).Any())
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
                    await File.WriteAllTextAsync(manifest, folder);
                }
                catch (IOException error) when (error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 2 })
                {
                    throw new IOException("SACD ISO import requires the EuFlo sacd_extract command on PATH.", error);
                }
            }
            else
                folder = await File.ReadAllTextAsync(manifest);
            foreach (string item in Directory.EnumerateFiles(folder, "*.dsf", SearchOption.AllDirectories).Order())
            {
                var track = await Media.ProbeAsync(item);
                track.Tags["sacd_image"] = path;
                track.Tags["edition"] = $"SACD {area}";
                result.Add(track);
            }
        }
        if (result.Count == 0)
            throw new IOException("The SACD extractor found no playable areas in this image. Check the Soundroom log for details.", extractionFailure);
        return result.ToArray();
    }
}
