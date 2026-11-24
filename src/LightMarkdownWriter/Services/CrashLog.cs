using System.IO;
using System.Reflection;

namespace LightMarkdownWriter.Services;

/// <summary>
/// Appends unhandled exceptions to %LOCALAPPDATA%\LightMarkdownWriter\crash.log so failures can be diagnosed
/// after the fact. Not a general logger (see PLAN.md): exceptions only, no document content.
/// </summary>
public static class CrashLog
{
    private const long MaxBytes = 1024 * 1024; // roll over to crash.old.log beyond this

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightMarkdownWriter", "crash.log");

    public static void Write(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes)
                File.Move(FilePath, Path.ChangeExtension(FilePath, ".old.log"), overwrite: true);

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
            File.AppendAllText(FilePath,
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} | v{version} | {source} ==={Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never cause a second failure.
        }
    }
}
