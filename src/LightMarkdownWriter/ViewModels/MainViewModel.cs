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

    public MainViewModel(IDialogService dialogs, IEnumerable<string> startupFiles)
    {
        _dialogs = dialogs;
        foreach (var path in startupFiles)
            OpenPath(path);
        if (Documents.Count == 0)
            New();
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

        var index = Documents.IndexOf(doc);
        Documents.Remove(doc);
        if (Documents.Count == 0)
            New();
        else if (ActiveDocument == doc || ActiveDocument is null)
            ActiveDocument = Documents[Math.Min(index, Documents.Count - 1)];
    }

    /// <summary>Asks about every unsaved tab. Returns false if the user cancelled.
    /// (Milestone 5 replaces this with session restore, so closing won't prompt.)</summary>
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
