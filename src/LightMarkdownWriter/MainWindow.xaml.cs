using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LightMarkdownWriter.Editing;
using LightMarkdownWriter.Services;
using LightMarkdownWriter.ViewModels;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LightMarkdownWriter;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _renderTimer;
    private DocumentViewModel? _watchedDocument;
    private ViewMode _appliedMode = ViewMode.Split;
    private double _editorShare = 0.5; // remembered split position
    private bool _closeConfirmed;
    private readonly bool _followSystemTheme;

    // Scroll sync state
    private long _ignoreEditorScrollUntil;            // set when we scroll the editor on the preview's behalf
    private bool _editorSyncQueued;
    private (double Line, string? Edge)? _previewPosition; // last position the user scrolled the preview to

    public MainWindow(MainViewModel viewModel, bool followSystemTheme = true)
    {
        _viewModel = viewModel;
        _followSystemTheme = followSystemTheme;
        DataContext = viewModel;
        InitializeComponent();

        // Breathing room between the line-number gutter and the text.
        Editor.TextArea.TextView.Margin = new Thickness(12, 0, 0, 0);
        // AvalonEdit's default link color is pure blue, unreadable in dark mode; follow the theme accent instead.
        Editor.TextArea.TextView.SetResourceReference(
            ICSharpCode.AvalonEdit.Rendering.TextView.LinkTextForegroundBrushProperty, "AccentTextFillColorPrimaryBrush");

        // Re-render shortly after typing pauses rather than on every keystroke.
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            RenderPreview();
        };

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Preview.MarkdownFileLinkClicked += path => _viewModel.OpenPath(path);
        Preview.PreviewScrolled += OnPreviewScrolled;
        Preview.KeyDown += OnPreviewKeyDown;
        Editor.TextArea.TextView.ScrollOffsetChanged += OnEditorScrollOffsetChanged;
        ApplicationThemeManager.Changed += OnThemeChanged;
        Loaded += OnLoaded;

        SetUpEditing();
    }

    // ---------- Editing: formatting shortcuts, list continuation, find/replace ----------

    private void SetUpEditing()
    {
        FindBar.Attach(Editor);

        // Window-level, so they also work from the preview (see OnPreviewKeyDown).
        AddKeyBinding(InputBindings, Key.F, ModifierKeys.Control, () => OpenFind(replace: false));
        AddKeyBinding(InputBindings, Key.H, ModifierKeys.Control, () => OpenFind(replace: true));
        AddKeyBinding(InputBindings, Key.F3, ModifierKeys.None, () => { EnsureEditorVisible(); FindBar.FindNext(forward: true); });
        AddKeyBinding(InputBindings, Key.F3, ModifierKeys.Shift, () => { EnsureEditorVisible(); FindBar.FindNext(forward: false); });

        // Editor-level: formatting only makes sense while typing in the editor.
        var editorBindings = Editor.TextArea.InputBindings;
        AddKeyBinding(editorBindings, Key.B, ModifierKeys.Control, () => MarkdownFormatting.ToggleInline(Editor, "**"));
        AddKeyBinding(editorBindings, Key.I, ModifierKeys.Control, () => MarkdownFormatting.ToggleInline(Editor, "*"));
        AddKeyBinding(editorBindings, Key.E, ModifierKeys.Control, () => MarkdownFormatting.ToggleInline(Editor, "`"));
        AddKeyBinding(editorBindings, Key.K, ModifierKeys.Control, () => MarkdownFormatting.InsertLink(Editor));
        AddKeyBinding(editorBindings, Key.K, ModifierKeys.Control | ModifierKeys.Shift, () => MarkdownFormatting.InsertCodeBlock(Editor));

        Editor.TextArea.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
                e.Handled = MarkdownFormatting.ContinueListOnEnter(Editor);
            else if (e.Key == Key.Escape && FindBar.IsOpen)
            {
                FindBar.Close();
                e.Handled = true;
            }
        };
    }

    private static void AddKeyBinding(InputBindingCollection bindings, Key key, ModifierKeys modifiers, Action action) =>
        bindings.Add(new KeyBinding(new CommunityToolkit.Mvvm.Input.RelayCommand(action), key, modifiers));

    private void EnsureEditorVisible()
    {
        if (_viewModel.ViewMode == ViewMode.Preview) _viewModel.ViewMode = ViewMode.Split;
    }

    private void OpenFind(bool replace)
    {
        EnsureEditorVisible();
        FindBar.Open(replace);
    }

    private void RunFormatting(Action<ICSharpCode.AvalonEdit.TextEditor> format)
    {
        EnsureEditorVisible();
        format(Editor);
        Editor.Focus();
    }

    private void OnBoldClick(object sender, RoutedEventArgs e) => RunFormatting(ed => MarkdownFormatting.ToggleInline(ed, "**"));
    private void OnItalicClick(object sender, RoutedEventArgs e) => RunFormatting(ed => MarkdownFormatting.ToggleInline(ed, "*"));
    private void OnInlineCodeClick(object sender, RoutedEventArgs e) => RunFormatting(ed => MarkdownFormatting.ToggleInline(ed, "`"));
    private void OnLinkClick(object sender, RoutedEventArgs e) => RunFormatting(MarkdownFormatting.InsertLink);
    private void OnCodeBlockClick(object sender, RoutedEventArgs e) => RunFormatting(MarkdownFormatting.InsertCodeBlock);
    private void OnFindClick(object sender, RoutedEventArgs e) => OpenFind(replace: false);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Follow Windows light/dark + accent changes live, keeping the Mica backdrop (unless forced via --theme).
        if (_followSystemTheme)
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
        ApplyPreviewTheme(ApplicationThemeManager.GetAppTheme());

        WatchDocument(_viewModel.ActiveDocument);
        ApplyViewMode(_viewModel.ViewMode);
        Editor.Focus();

        try
        {
            await Preview.InitializeAsync();
            RenderPreview();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            new Services.DialogService().ShowError(
                "The Microsoft Edge WebView2 Runtime is required for the preview.\nInstall it from https://go.microsoft.com/fwlink/p/?LinkId=2124703");
        }
    }

    private void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent) =>
        ApplyPreviewTheme(theme);

    private void ApplyPreviewTheme(ApplicationTheme theme)
    {
        var dark = theme == ApplicationTheme.Dark;
        Editor.SyntaxHighlighting = MarkdownHighlighting.Get(dark);
        // Same resource as the editor card's background, so both panes are the exact same color.
        var background = TryFindResource("SolidBackgroundFillColorTertiary") is System.Windows.Media.Color c
            ? c
            : dark ? System.Windows.Media.Color.FromRgb(0x28, 0x28, 0x28) : System.Windows.Media.Color.FromRgb(0xF9, 0xF9, 0xF9);
        // Theme-adjusted accent (lighter in dark mode) — the same brush the editor uses for links.
        var accent = (TryFindResource("AccentTextFillColorPrimaryBrush") as System.Windows.Media.SolidColorBrush)?.Color;
        Preview.SetTheme(dark, background, accent);
    }

    /// <summary>
    /// The WebView2 is a native child window, so while it has focus WPF never routes keys to the window's
    /// InputBindings. The control does forward accelerator keys as a KeyDown on itself; match those against
    /// our own KeyBindings so every shortcut (Ctrl+S, Ctrl+1..3, Ctrl+Tab...) works from the preview too.
    /// </summary>
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        foreach (var binding in InputBindings.OfType<System.Windows.Input.KeyBinding>())
        {
            if (binding.Key != key || binding.Modifiers != modifiers) continue;
            if (binding.Command?.CanExecute(binding.CommandParameter) == true)
                binding.Command.Execute(binding.CommandParameter);
            e.Handled = true;
            return;
        }
    }

    // ---------- Scroll sync ----------

    private void OnEditorScrollOffsetChanged(object? sender, EventArgs e)
    {
        if (Environment.TickCount64 < _ignoreEditorScrollUntil) return;
        _previewPosition = null; // the editor is now the one being driven
        if (_editorSyncQueued) return;

        // Coalesce bursts of scroll events into one message per frame.
        _editorSyncQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _editorSyncQueued = false;
            if (EditorTopPosition() is var (line, edge))
                Preview.SyncToLine(line, edge);
        }, DispatcherPriority.Render);
    }

    /// <summary>The fractional 0-based source line at the top of the editor viewport.</summary>
    private (double Line, string? Edge)? EditorTopPosition()
    {
        var document = Editor.Document;
        var view = Editor.TextArea.TextView;
        if (document is null) return null;

        var y = view.ScrollOffset.Y;
        if (y <= 0.5) return (0, "top");
        if (y + view.ActualHeight >= view.DocumentHeight - 1) return (document.LineCount, "bottom");

        // With word wrap a source line can span several visual rows, so measure within the line's full height.
        var line = view.GetDocumentLineByVisualTop(y);
        var top = view.GetVisualTopByDocumentLine(line.LineNumber);
        var bottom = line.LineNumber < document.LineCount
            ? view.GetVisualTopByDocumentLine(line.LineNumber + 1)
            : view.DocumentHeight;
        var fraction = bottom > top ? (y - top) / (bottom - top) : 0;
        return (line.LineNumber - 1 + fraction, null);
    }

    private void OnPreviewScrolled(double line, string? edge)
    {
        _previewPosition = (line, edge);
        if (_appliedMode == ViewMode.Split)
            ScrollEditorTo(line, edge);
    }

    private void ScrollEditorTo(double line, string? edge)
    {
        var document = Editor.Document;
        var view = Editor.TextArea.TextView;
        if (document is null) return;

        double y;
        if (edge == "top") y = 0;
        else if (edge == "bottom") y = view.DocumentHeight;
        else
        {
            var number = Math.Clamp((int)Math.Floor(line) + 1, 1, document.LineCount);
            var top = view.GetVisualTopByDocumentLine(number);
            var bottom = number < document.LineCount ? view.GetVisualTopByDocumentLine(number + 1) : view.DocumentHeight;
            y = top + (bottom - top) * (line - Math.Floor(line));
        }

        // Our own scroll will raise ScrollOffsetChanged; don't echo it back to the preview.
        _ignoreEditorScrollUntil = Environment.TickCount64 + 150;
        Editor.ScrollToVerticalOffset(y);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ActiveDocument):
                WatchDocument(_viewModel.ActiveDocument);
                RenderPreview();
                Dispatcher.BeginInvoke(() => Editor.Focus(), DispatcherPriority.Input);
                break;
            case nameof(MainViewModel.ViewMode):
                ApplyViewMode(_viewModel.ViewMode);
                break;
        }
    }

    private void WatchDocument(DocumentViewModel? doc)
    {
        if (_watchedDocument is not null)
        {
            _watchedDocument.Document.TextChanged -= OnDocumentTextChanged;
            _watchedDocument.PropertyChanged -= OnDocumentPropertyChanged;
        }
        _watchedDocument = doc;
        if (doc is not null)
        {
            doc.Document.TextChanged += OnDocumentTextChanged;
            doc.PropertyChanged += OnDocumentPropertyChanged;
        }
    }

    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // "Save as" to another folder changes where relative images resolve from.
        if (e.PropertyName == nameof(DocumentViewModel.FilePath))
            RenderPreview();
    }

    private void RenderPreview()
    {
        _renderTimer.Stop();
        if (_appliedMode == ViewMode.Editor || _watchedDocument is not { } doc) return;

        var html = MarkdownRenderer.ToHtml(doc.Document.Text);
        var folder = doc.FilePath is null ? null : Path.GetDirectoryName(doc.FilePath);
        Preview.ShowHtml(html, folder, doc.Document.LineCount);
    }

    private void ApplyViewMode(ViewMode mode)
    {
        // Remember where the user dragged the splitter before leaving split view.
        if (_appliedMode == ViewMode.Split)
        {
            var total = EditorColumn.ActualWidth + PreviewColumn.ActualWidth;
            if (total > 0) _editorShare = EditorColumn.ActualWidth / total;
        }

        var showEditor = mode != ViewMode.Preview;
        var showPreview = mode != ViewMode.Editor;

        EditorCard.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
        PreviewCard.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = mode == ViewMode.Split ? Visibility.Visible : Visibility.Collapsed;

        EditorColumn.MinWidth = PreviewColumn.MinWidth = mode == ViewMode.Split ? 200 : 0;
        EditorColumn.Width = mode switch
        {
            ViewMode.Split => new GridLength(_editorShare, GridUnitType.Star),
            ViewMode.Editor => new GridLength(1, GridUnitType.Star),
            _ => new GridLength(0),
        };
        PreviewColumn.Width = mode switch
        {
            ViewMode.Split => new GridLength(1 - _editorShare, GridUnitType.Star),
            ViewMode.Preview => new GridLength(1, GridUnitType.Star),
            _ => new GridLength(0),
        };

        var previewWasHidden = _appliedMode == ViewMode.Editor;
        var editorWasHidden = _appliedMode == ViewMode.Preview;
        _appliedMode = mode;
        if (showPreview && previewWasHidden) RenderPreview(); // preview skips work while hidden; render carries the editor's position
        if (showEditor)
        {
            Dispatcher.BeginInvoke(() =>
            {
                // Coming back from preview-only: open the editor where the user was reading.
                if (editorWasHidden && _previewPosition is var (line, edge)) ScrollEditorTo(line, edge);
                Editor.Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed) return;

        // The confirmation is async, so cancel this close and re-issue it once the user has answered.
        e.Cancel = true;
        if (await _viewModel.ConfirmCloseAllAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ApplicationThemeManager.Changed -= OnThemeChanged;
        base.OnClosed(e);
    }
}
