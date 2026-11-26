using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Soundroom;

internal static class Diagnostics
{
    private static readonly object Gate = new();
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("MP4A_DATA_DIRECTORY") is { Length: > 0 } isolated
            ? Path.GetFullPath(isolated)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundroom");
    public static string LogFile => Path.Combine(DataDirectory, "Logs", "Soundroom.log");

    public static string Report(string operation, Exception error)
    {
        Write(operation, error);
        string detail = error switch
        {
            UnauthorizedAccessException => "Windows denied access. Check file and folder permissions.",
            FileNotFoundException => "The file could not be found. Check whether it was moved or disconnected.",
            COMException { HResult: unchecked((int)0x88890004) } => "The audio device disconnected. Select an available output and try again.",
            COMException { HResult: unchecked((int)0x8889000A) } => "Another app is using this output. Close its audio stream or choose a different output.",
            COMException { HResult: unchecked((int)0x8889000E) } => "Exclusive access is disabled for this output in Windows Sound settings.",
            Win32Exception { NativeErrorCode: 2 } => "A required tool could not be started. Check the installation and PATH.",
            _ => error.Message
        };
        string line = detail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        if (line.Length == 0)
            return $"{operation} failed. See the Soundroom log.";
        return $"{operation}: {(line.Length > 320 ? line[..320] + "…" : line)}";
    }

    public static void Write(string operation, Exception error)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length >= 1024 * 1024)
                    File.Move(LogFile, LogFile + ".1", overwrite: true);
                File.AppendAllText(LogFile, $"[{DateTimeOffset.Now:O}] {operation}\n{error}\n\n");
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.WriteLine($"{operation}: {error}");
            }
        }
    }

    public static void Cleanup(string operation, Action action)
    {
        try { action(); }
        catch (Exception error) { Write(operation, error); }
    }
}
