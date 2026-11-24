using System.IO;
using System.Text.Json;

namespace LightMarkdownWriter.Services;

/// <summary>
/// Most-recently-used files (opened or saved), newest first, kept in %LOCALAPPDATA%\LightMarkdownWriter\recent.json.
/// Missing files are kept in storage (a drive may just be unplugged) and filtered out when shown.
/// </summary>
public sealed class RecentFilesService
{
    public const int MaxItems = 15;

    private readonly string _file;
    private readonly List<string> _items;

    public RecentFilesService(string? file = null)
    {
        _file = file ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightMarkdownWriter", "recent.json");
        _items = Load(_file);
    }

    public event EventHandler? Changed;

    /// <summary>All remembered paths, newest first (including ones that no longer exist).</summary>
    public IReadOnlyList<string> Items => _items;

    /// <summary>The paths worth showing: newest first, only files that still exist.</summary>
    public IReadOnlyList<string> Existing => _items.Where(File.Exists).ToList();

    public void Add(string path)
    {
        var full = Path.GetFullPath(path);
        _items.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, full);
        if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        Save();
    }

    public void Remove(string path)
    {
        if (_items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0) Save();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Save();
    }

    private static List<string> Load(string file)
    {
        try
        {
            if (File.Exists(file))
                return (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? [])
                    .Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxItems).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable list: start empty; the next change rewrites it.
        }
        return [];
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory list still works for this run.
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
