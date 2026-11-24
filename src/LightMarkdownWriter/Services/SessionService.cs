using System.IO;
using System.Text.Json;

namespace LightMarkdownWriter.Services;

/// <summary>One tab as stored in session.json.</summary>
public sealed record SessionTab
{
    public required Guid Id { get; init; }
    public string? FilePath { get; init; }
    public string? UntitledName { get; init; }
    /// <summary>True when backups/{Id}.md holds unsaved text for this tab.</summary>
    public bool HasBackup { get; init; }
    public string? EncodingName { get; init; }
    /// <summary>The file's timestamp when we last loaded/saved it, to detect edits made while the app was closed.</summary>
    public DateTime? FileWriteTimeUtc { get; init; }
    public int CaretOffset { get; init; }
    public double ScrollLine { get; init; }
}

public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

public sealed record Session
{
    public List<SessionTab> Tabs { get; init; } = [];
    public int ActiveIndex { get; init; }
    public string ViewMode { get; init; } = "Split";
    public double EditorShare { get; init; } = 0.5;
    public WindowPlacement? Window { get; init; }
}

/// <summary>
/// Persists open tabs, unsaved text and window state under %LOCALAPPDATA%\LightMarkdownWriter, so closing the
/// app never asks to save (Notepad++ style). Writes are atomic: temp file, then rename.
/// </summary>
public sealed class SessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _sessionFile;
    private readonly string _backupFolder;

    public SessionService()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightMarkdownWriter");
        _sessionFile = Path.Combine(root, "session.json");
        _backupFolder = Path.Combine(root, "backups");
    }

    public Session Load()
    {
        try
        {
            if (File.Exists(_sessionFile))
                return JsonSerializer.Deserialize<Session>(File.ReadAllText(_sessionFile), JsonOptions) ?? new Session();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt or unreadable session just means starting fresh; backups stay on disk untouched.
        }
        return new Session();
    }

    public string? ReadBackup(Guid id)
    {
        var path = BackupPath(id);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes a tab's unsaved text. Backups are always UTF-8; the tab's own encoding is used when it's saved.</summary>
    public void WriteBackup(Guid id, string text)
    {
        Directory.CreateDirectory(_backupFolder);
        WriteAtomic(BackupPath(id), text);
    }

    /// <summary>Saves the session and deletes backups no tab refers to any more.</summary>
    public void Save(Session session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_sessionFile)!);
        WriteAtomic(_sessionFile, JsonSerializer.Serialize(session, JsonOptions));

        if (!Directory.Exists(_backupFolder)) return;
        var keep = session.Tabs.Where(t => t.HasBackup).Select(t => t.Id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_backupFolder, "*.md"))
        {
            if (keep.Contains(Path.GetFileNameWithoutExtension(file))) continue;
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* retried next save */ }
        }
    }

    private string BackupPath(Guid id) => Path.Combine(_backupFolder, id.ToString("N") + ".md");

    private static void WriteAtomic(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
