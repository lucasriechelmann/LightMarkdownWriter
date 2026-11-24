using System.ComponentModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using ICSharpCode.AvalonEdit.Document;

namespace LightMarkdownWriter.ViewModels;

/// <summary>One open document (one tab). Owns the AvalonEdit TextDocument so undo history survives tab switches.</summary>
public partial class DocumentViewModel : ObservableObject
{
    private static int _untitledCounter;
    private readonly string _untitledName;

    public TextDocument Document { get; }

    public Encoding Encoding { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string? _filePath;

    [ObservableProperty]
    private bool _isDirty;

    public string DisplayName => FilePath is null ? _untitledName : Path.GetFileName(FilePath);

    /// <summary>True for a fresh "Untitled" tab nobody has typed into — safe to replace when opening a file.</summary>
    public bool IsPristineUntitled => FilePath is null && Document.TextLength == 0 && !IsDirty;

    private DocumentViewModel(string text, string? filePath, Encoding encoding)
    {
        _untitledName = filePath is null ? $"Untitled {++_untitledCounter}" : string.Empty;
        _filePath = filePath;
        Encoding = encoding;
        Document = new TextDocument(text);
        Document.UndoStack.PropertyChanged += OnUndoStackChanged;
    }

    public static DocumentViewModel CreateNew() => new(string.Empty, null, new UTF8Encoding(false));

    public static DocumentViewModel Load(string path)
    {
        // Default to UTF-8 (no BOM); a BOM in the file overrides it, and we keep whatever we detected for saving.
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return new DocumentViewModel(text, Path.GetFullPath(path), reader.CurrentEncoding);
    }

    public void SaveTo(string path)
    {
        File.WriteAllText(path, Document.Text, Encoding);
        FilePath = Path.GetFullPath(path);
        Document.UndoStack.MarkAsOriginalFile();
    }

    private void OnUndoStackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
            IsDirty = !Document.UndoStack.IsOriginalFile;
    }
}
