using System;
using System.IO;

namespace SnapTool;

/// <summary>Tiny append-only log at %APPDATA%\SnapTool\snaptool.log (trimmed when it grows past 1 MB).</summary>
internal static class Log
{
    static readonly object Gate = new();
    static string FilePath => Path.Combine(SettingsStore.Folder, "snaptool.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* logging must never break the app */ }
    }
}
