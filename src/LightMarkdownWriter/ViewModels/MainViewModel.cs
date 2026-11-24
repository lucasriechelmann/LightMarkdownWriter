using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightMarkdownWriter.Services;

namespace LightMarkdownWriter.ViewModels;

public enum ViewMode { Editor, Split, Preview }

public partial class MainViewModel : ObservableObject
{
    private const string AppName = "LightMarkdownWriter";
    private readonly IDialogService _dialogs;

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private DocumentViewModel? _activeDocument;

    [ObservableProperty]
    private ViewMode _viewMode = ViewMode.Split;

    [RelayCommand]
    private void SetViewMode(ViewMode mode) => ViewMode = mode;

    [RelayCommand]
    private void NextTab() => MoveTab(+1);

    [RelayCommand]
    private void PreviousTab() => MoveTab(-1);

    private void MoveTab(int delta)
    {
        if (Documents.Count < 2 || ActiveDocument is null) return;
        var index = (Documents.IndexOf(ActiveDocument) + delta + Documents.Count) % Documents.Count;
        ActiveDocument = Documents[index];
    }

    public string WindowTitle => ActiveDocument is null
        ? AppName
        : $"{(ActiveDocument.IsDirty ? "• " : "")}{ActiveDocument.DisplayName} - {AppName}";

    public MainViewModel(IDialogService dialogs, IEnumerable<string> startupFiles,
                         SessionService? sessionService = null, Session? session = null,
                         RecentFilesService? recentFiles = null)
    {
        _dialogs = dialogs;
        _sessionService = sessionService;
        RecentFiles = recentFiles;
        if (session is not null) RestoreSession(session);

        foreach (var path in startupFiles)
            OpenPath(path);
        if (Documents.Count == 0)
            New();

        CheckExternalChanges(); // files edited while the app was closed
    }

    // ---------- Session (restore on start, persist while running and on close) ----------

    private readonly SessionService? _sessionService;
    private readonly HashSet<Guid> _backedUp = []; // tabs whose backup file is current

    private void RestoreSession(Session session)
    {
        foreach (var tab in session.Tabs)
        {
            var backup = tab.HasBackup ? _sessionService?.ReadBackup(tab.Id) : null;
            if (DocumentViewModel.Restore(tab, backup) is not { } doc) continue; // file deleted and nothing unsaved
            if (backup is not null) _backedUp.Add(doc.Id);
            Documents.Add(doc);
        }

        if (Documents.Count > 0)
            ActiveDocument = Documents[Math.Clamp(session.ActiveIndex, 0, Documents.Count - 1)];
        if (Enum.TryParse<ViewMode>(session.ViewMode, out var mode))
            ViewMode = mode;
    }

    /// <summary>
    /// Writes backups for tabs with unsaved text (only those that changed) and the session file.
    /// <paramref name="decorate"/> lets the window add its own state (placement, splitter). Throws on I/O failure.
    /// </summary>
    public void PersistSession(Func<Session, Session>? decorate = null)
    {
        if (_sessionService is null) return;

        var tabs = new List<SessionTab>(Documents.Count);
        foreach (var doc in Documents)
        {
            var needsBackup = doc.IsDirty;
            if (needsBackup && (doc.BackupStale || !_backedUp.Contains(doc.Id)))
            {
                _sessionService.WriteBackup(doc.Id, doc.Document.Text);
                doc.BackupStale = false;
                _backedUp.Add(doc.Id);
            }
            else if (!needsBackup)
            {
                _backedUp.Remove(doc.Id); // Save() deletes the orphaned file
            }
            tabs.Add(doc.ToSessionTab(hasBackup: needsBackup));
        }

        var session = new Session
        {
            Tabs = tabs,
            ActiveIndex = ActiveDocument is null ? 0 : Math.Max(0, Documents.IndexOf(ActiveDocument)),
            ViewMode = ViewMode.ToString(),
        };
        _sessionService.Save(decorate?.Invoke(session) ?? session);
    }

    // ---------- Files changed outside the app ----------

    public void CheckExternalChanges()
    {
        foreach (var doc in Documents)
        {
            try { doc.CheckDisk(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* try again next time */ }
        }
    }

    [RelayCommand]
    private void ReloadFromDisk()
    {
        try { ActiveDocument?.ReloadFromDisk(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"Could not reload {ActiveDocument?.FilePath}:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private void KeepMyVersion() => ActiveDocument?.IgnoreExternalChange();

    // ---------- Recent files (9.1) ----------

    public RecentFilesService? RecentFiles { get; }

    [RelayCommand]
    private void OpenRecent(string path)
    {
        if (!File.Exists(path))
        {
            RecentFiles?.Remove(path);
            _dialogs.ShowError($"{path}\n\nThis file no longer exists, so it was removed from the recent list.");
            return;
        }
        OpenPath(path);
    }

    [RelayCommand]
    private void ClearRecent() => RecentFiles?.Clear();

    // ---------- Reopen closed tab (9.2) ----------

    private const int MaxClosedTabs = 20;

    // Same snapshot format as the session: the tab, plus its text if unsaved edits were discarded on close.
    private readonly List<(SessionTab Tab, string? UnsavedText)> _closedTabs = [];

    public bool CanReopenClosedTab => _closedTabs.Count > 0;

    private void RememberClosed(DocumentViewModel doc)
    {
        if (doc.IsPristineUntitled) return; // nothing worth bringing back
        _closedTabs.Add((doc.ToSessionTab(hasBackup: doc.IsDirty), doc.IsDirty ? doc.Document.Text : null));
        if (_closedTabs.Count > MaxClosedTabs) _closedTabs.RemoveAt(0);
        OnPropertyChanged(nameof(CanReopenClosedTab));
    }

    /// <summary>Ctrl+Shift+T: brings back the most recently closed tab, unsaved text included.</summary>
    [RelayCommand]
    private void ReopenClosedTab()
    {
        while (_closedTabs.Count > 0)
        {
            var (tab, text) = _closedTabs[^1];
            _closedTabs.RemoveAt(_closedTabs.Count - 1);
            OnPropertyChanged(nameof(CanReopenClosedTab));

            var open = tab.FilePath is null ? null : Documents.FirstOrDefault(d =>
                string.Equals(d.FilePath, tab.FilePath, StringComparison.OrdinalIgnoreCase));
            if (open is not null)
            {
                ActiveDocument = open;
                return;
            }

            // A fresh Id: the closed tab's backup (if any) is gone, and ids must stay unique.
            if (DocumentViewModel.Restore(tab with { Id = Guid.NewGuid() }, text) is not { } doc)
                continue; // file deleted and nothing unsaved to restore: try the one before it

            if (Documents.Count == 1 && Documents[0].IsPristineUntitled)
                Documents[0] = doc;
            else
                Documents.Add(doc);
            ActiveDocument = doc;
            return;
        }
    }

    // ---------- Status bar: encoding and line endings ----------

    /// <summary>"utf-8" or "utf-8-bom".</summary>
    [RelayCommand]
    private void SetEncoding(string name) =>
        ActiveDocument?.ChangeEncoding(new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: name == "utf-8-bom"));

    /// <summary>"CRLF" or "LF". One undo step; marks the tab unsaved.</summary>
    [RelayCommand]
    private void ConvertLineEndings(string kind)
    {
        if (ActiveDocument is { } doc)
            Editing.TextStats.ConvertLineEndings(doc.Document, kind == "LF" ? "\n" : "\r\n");
    }

    partial void OnActiveDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnActiveDocumentPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnActiveDocumentPropertyChanged;
    }

    private void OnActiveDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.IsDirty) or nameof(DocumentViewModel.DisplayName))
            OnPropertyChanged(nameof(WindowTitle));
    }

    [RelayCommand]
    private void New()
    {
        var doc = DocumentViewModel.CreateNew();
        Documents.Add(doc);
        ActiveDocument = doc;
    }

    [RelayCommand]
    private void Open()
    {
        foreach (var path in _dialogs.ShowOpenDialog())
            OpenPath(path);
    }

    public void OpenPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var existing = Documents.FirstOrDefault(d =>
            string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveDocument = existing;
            return;
        }

        DocumentViewModel doc;
        try
        {
            doc = DocumentViewModel.Load(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"Could not open {fullPath}:\n{ex.Message}");
            return;
        }

        // Opening a file into a blank window replaces the empty "Untitled" tab, like Notepad.
        if (Documents.Count == 1 && Documents[0].IsPristineUntitled)
            Documents[0] = doc;
        else
            Documents.Add(doc);
        ActiveDocument = doc;
        RecentFiles?.Add(fullPath);
    }

    [RelayCommand]
    private void Save() => TrySave(ActiveDocument, forceDialog: false);

    [RelayCommand]
    private void SaveAs() => TrySave(ActiveDocument, forceDialog: true);

    private bool TrySave(DocumentViewModel? doc, bool forceDialog)
    {
        if (doc is null) return false;

        var path = doc.FilePath;
        if (path is null || forceDialog)
        {
            var suggested = doc.FilePath is null ? doc.DisplayName + ".md" : Path.GetFileName(doc.FilePath);
            path = _dialogs.ShowSaveDialog(suggested);
            if (path is null) return false;
        }

        try
        {
            doc.SaveTo(path);
            RecentFiles?.Add(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError($"Could not save {path}:\n{ex.Message}");
            return false;
        }
    }

    [RelayCommand]
    private async Task CloseTab(DocumentViewModel? doc)
    {
        doc ??= ActiveDocument;
        if (doc is null || !await ConfirmDiscardAsync(doc)) return;

        RememberClosed(doc);
        var index = Documents.IndexOf(doc);
        Documents.Remove(doc);
        if (Documents.Count == 0)
            New();
        else if (ActiveDocument == doc || ActiveDocument is null)
            ActiveDocument = Documents[Math.Min(index, Documents.Count - 1)];
    }

    /// <summary>Asks about every unsaved tab. Returns false if the user cancelled.
    /// Only used as a fallback when the session can't be saved; normally closing keeps unsaved text.</summary>
    public async Task<bool> ConfirmCloseAllAsync()
    {
        foreach (var doc in Documents.Where(d => d.IsDirty).ToList())
        {
            ActiveDocument = doc;
            if (!await ConfirmDiscardAsync(doc)) return false;
        }
        return true;
    }

    private async Task<bool> ConfirmDiscardAsync(DocumentViewModel doc)
    {
        if (!doc.IsDirty) return true;
        return await _dialogs.AskSaveChangesAsync(doc.DisplayName) switch
        {
            SaveChoice.Save => TrySave(doc, forceDialog: false),
            SaveChoice.Discard => true,
            _ => false,
        };
    }
}
