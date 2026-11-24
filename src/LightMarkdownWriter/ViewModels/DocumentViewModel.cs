using System.ComponentModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using ICSharpCode.AvalonEdit.Document;
using LightMarkdownWriter.Services;

namespace LightMarkdownWriter.ViewModels;

public enum ExternalChange { None, Modified, Deleted }

/// <summary>One open document (one tab). Owns the AvalonEdit TextDocument so undo history survives tab switches.</summary>
public partial class DocumentViewModel : ObservableObject
{
    private static int _untitledCounter;
    private readonly string _untitledName;
    private DateTime? _lastKnownWriteTimeUtc; // disk timestamp when we last loaded/saved; null for untitled

    /// <summary>Stable identity, used to name this tab's unsaved-text backup.</summary>
    public Guid Id { get; }

    public TextDocument Document { get; }

    private Encoding _encoding = Utf8NoBom;

    /// <summary>The encoding the file is saved with (detected on load, kept on save, changed from the status bar).</summary>
    public Encoding Encoding
    {
        get => _encoding;
        private set
        {
            if (SetProperty(ref _encoding, value)) OnPropertyChanged(nameof(EncodingLabel));
        }
    }

    public string EncodingLabel => Encoding switch
    {
        UTF8Encoding utf8 => utf8.GetPreamble().Length > 0 ? "UTF-8 with BOM" : "UTF-8",
        UnicodeEncoding unicode => unicode.GetPreamble() is [0xFE, 0xFF, ..] ? "UTF-16 BE" : "UTF-16 LE",
        _ => Encoding.WebName.ToUpperInvariant(),
    };

    /// <summary>
    /// Saves with a different encoding from now on. The text is unchanged but the bytes on disk will differ, so
    /// the tab counts as unsaved until it's saved.
    /// </summary>
    public void ChangeEncoding(Encoding encoding)
    {
        if (EncodingToName(encoding) == EncodingToName(Encoding)) return;
        Encoding = encoding;
        Document.UndoStack.DiscardOriginalFileMarker();
        BackupStale = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string? _filePath;

    [ObservableProperty]
    private bool _isDirty;

    /// <summary>Set when the file changed on disk while this tab had unsaved edits (or the file was deleted).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExternalChange), nameof(IsDeletedOnDisk), nameof(IsModifiedOnDisk), nameof(ExternalChangeMessage))]
    private ExternalChange _externalChange;

    public bool HasExternalChange => ExternalChange != ExternalChange.None;
    public bool IsModifiedOnDisk => ExternalChange == ExternalChange.Modified;
    public bool IsDeletedOnDisk => ExternalChange == ExternalChange.Deleted;

    public string ExternalChangeMessage => ExternalChange switch
    {
        ExternalChange.Modified => "Another program changed this file, and you have unsaved edits here.",
        ExternalChange.Deleted => "This file was deleted or moved. Your text is still here; saving recreates it.",
        _ => "",
    };

    /// <summary>True when the text changed since the last backup was written.</summary>
    public bool BackupStale { get; set; }

    // Remembered per tab, since the editor control is shared by all tabs.
    public int CaretOffset { get; set; }
    public double ScrollLine { get; set; }

    public string DisplayName => FilePath is null ? _untitledName : Path.GetFileName(FilePath);

    /// <summary>True for a fresh "Untitled" tab nobody has typed into — safe to replace when opening a file.</summary>
    public bool IsPristineUntitled => FilePath is null && Document.TextLength == 0 && !IsDirty;

    private DocumentViewModel(Guid id, string text, string? filePath, Encoding encoding, string? untitledName)
    {
        Id = id;
        _filePath = filePath;
        Encoding = encoding;
        _untitledName = filePath is not null ? string.Empty : untitledName ?? $"Untitled {++_untitledCounter}";
        ReserveUntitledNumber(_untitledName);

        Document = new TextDocument(text);
        Document.UndoStack.PropertyChanged += OnUndoStackChanged;
        Document.TextChanged += (_, _) => BackupStale = true;
    }

    public static DocumentViewModel CreateNew() => new(Guid.NewGuid(), string.Empty, null, Utf8NoBom, null);

    public static DocumentViewModel Load(string path)
    {
        var (text, encoding) = ReadFile(path);
        var doc = new DocumentViewModel(Guid.NewGuid(), text, Path.GetFullPath(path), encoding, null);
        doc._lastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(doc.FilePath!);
        return doc;
    }

    /// <summary>Rebuilds a tab from the session. Returns null if there's nothing left to show (file gone, no backup).</summary>
    public static DocumentViewModel? Restore(SessionTab tab, string? backupText)
    {
        var encoding = TryGetEncoding(tab.EncodingName) ?? Utf8NoBom;
        DocumentViewModel doc;

        if (backupText is not null)
        {
            // Unsaved edits: show them, and mark the tab dirty (nothing in its undo history matches the disk).
            doc = new DocumentViewModel(tab.Id, backupText, tab.FilePath, encoding, tab.UntitledName);
            doc.Document.UndoStack.DiscardOriginalFileMarker();
            doc.IsDirty = true;
            // The timestamp from when the backup was taken, so CheckDisk() notices edits made while we were closed.
            if (tab.FilePath is not null)
                doc._lastKnownWriteTimeUtc = tab.FileWriteTimeUtc
                    ?? (File.Exists(tab.FilePath) ? File.GetLastWriteTimeUtc(tab.FilePath) : DateTime.MinValue);
        }
        else if (tab.FilePath is not null)
        {
            if (!File.Exists(tab.FilePath)) return null;
            var (text, fileEncoding) = ReadFile(tab.FilePath);
            doc = new DocumentViewModel(tab.Id, text, tab.FilePath, fileEncoding, null);
            doc._lastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(tab.FilePath);
        }
        else
        {
            doc = new DocumentViewModel(tab.Id, string.Empty, null, encoding, tab.UntitledName);
        }

        doc.CaretOffset = Math.Clamp(tab.CaretOffset, 0, doc.Document.TextLength);
        doc.ScrollLine = tab.ScrollLine;
        doc.BackupStale = false;
        return doc;
    }

    public SessionTab ToSessionTab(bool hasBackup) => new()
    {
        Id = Id,
        FilePath = FilePath,
        UntitledName = FilePath is null ? _untitledName : null,
        HasBackup = hasBackup,
        EncodingName = EncodingToName(Encoding),
        FileWriteTimeUtc = _lastKnownWriteTimeUtc,
        CaretOffset = CaretOffset,
        ScrollLine = ScrollLine,
    };

    public void SaveTo(string path)
    {
        File.WriteAllText(path, Document.Text, Encoding);
        FilePath = Path.GetFullPath(path);
        _lastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath);
        ExternalChange = ExternalChange.None;
        Document.UndoStack.MarkAsOriginalFile();
    }

    /// <summary>
    /// Compares the file on disk with what we last loaded/saved. Clean tabs reload silently;
    /// tabs with unsaved edits get <see cref="ExternalChange"/> set so the user can decide.
    /// </summary>
    public void CheckDisk()
    {
        if (FilePath is null || _lastKnownWriteTimeUtc is null) return;

        if (!File.Exists(FilePath))
        {
            ExternalChange = ExternalChange.Deleted;
            return;
        }

        var writeTime = File.GetLastWriteTimeUtc(FilePath);
        if (writeTime == _lastKnownWriteTimeUtc) return;

        if (IsDirty)
            ExternalChange = ExternalChange.Modified;
        else
            ReloadFromDisk();
    }

    public void ReloadFromDisk()
    {
        if (FilePath is null || !File.Exists(FilePath)) return;
        var (text, encoding) = ReadFile(FilePath);
        Encoding = encoding;

        // Replace only the span that differs (common prefix/suffix kept). Replacing everything would move the
        // caret to the end and scroll the editor away; a small replace leaves the user where they were.
        var old = Document.Text;
        var max = Math.Min(old.Length, text.Length);
        var prefix = 0;
        while (prefix < max && old[prefix] == text[prefix]) prefix++;
        var suffix = 0;
        while (suffix < max - prefix && old[old.Length - 1 - suffix] == text[text.Length - 1 - suffix]) suffix++;
        if (prefix != old.Length || old.Length != text.Length)
            Document.Replace(prefix, old.Length - prefix - suffix, text.Substring(prefix, text.Length - prefix - suffix));

        Document.UndoStack.MarkAsOriginalFile();
        _lastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath);
        ExternalChange = ExternalChange.None;
    }

    /// <summary>"Keep my version": stop warning until the file changes again.</summary>
    public void IgnoreExternalChange()
    {
        if (FilePath is not null && File.Exists(FilePath))
        {
            _lastKnownWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath);
        }
        else
        {
            // Deleted but kept: the text now exists only here, so mark it unsaved; saving recreates the file.
            _lastKnownWriteTimeUtc = null;
            Document.UndoStack.DiscardOriginalFileMarker();
        }
        ExternalChange = ExternalChange.None;
    }

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private static (string Text, Encoding Encoding) ReadFile(string path)
    {
        // Default to UTF-8 (no BOM); a BOM in the file overrides it, and we keep whatever we detected for saving.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return (text, reader.CurrentEncoding);
    }

    // WebName alone loses whether the file had a BOM ("utf-8" either way), and GetEncoding("utf-8") writes one,
    // so the BOM is recorded explicitly — otherwise restoring would silently add a BOM on the next save.
    private static string EncodingToName(Encoding encoding) =>
        encoding.WebName + (encoding.GetPreamble().Length > 0 ? "+bom" : "");

    private static Encoding? TryGetEncoding(string? name)
    {
        switch (name)
        {
            case null: return null;
            case "utf-8": return Utf8NoBom;
            case "utf-8+bom": return new UTF8Encoding(true);
        }
        try { return Encoding.GetEncoding(name.Replace("+bom", "", StringComparison.Ordinal)); }
        catch (ArgumentException) { return null; }
    }

    // Keep new "Untitled N" names unique after restoring tabs named "Untitled 3" etc.
    private static void ReserveUntitledNumber(string name)
    {
        if (name.StartsWith("Untitled ", StringComparison.Ordinal) && int.TryParse(name[9..], out var n))
            _untitledCounter = Math.Max(_untitledCounter, n);
    }

    private void OnUndoStackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
            IsDirty = !Document.UndoStack.IsOriginalFile;
    }
}
