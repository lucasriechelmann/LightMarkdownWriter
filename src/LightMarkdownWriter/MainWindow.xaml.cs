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
    private readonly AppSettings _settings;
    private AppTheme? _themeOverride; // --theme on the command line wins until the user changes the setting
    private bool _watchingSystemTheme;
    private Views.SettingsWindow? _settingsWindow;

    // Scroll sync state
    private long _ignoreEditorScrollUntil;            // set when we scroll the editor on the preview's behalf
    private bool _editorSyncQueued;
    private (double Line, string? Edge)? _previewPosition; // last position the user scrolled the preview to

    // Session persistence: saved ~2 s after changes (crash safety) and on close.
    private readonly DispatcherTimer _sessionTimer;
    private bool _positionSaveQueued;

    public MainWindow(MainViewModel viewModel, AppSettings settings, AppTheme? themeOverride = null, Session? session = null)
    {
        _viewModel = viewModel;
        _settings = settings;
        _themeOverride = themeOverride;
        DataContext = viewModel;
        InitializeComponent();

        ApplyEditorSettings();
        settings.PropertyChanged += OnSettingsChanged;

        RestorePlacement(session);
        _sessionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _sessionTimer.Tick += (_, _) =>
        {
            _sessionTimer.Stop();
            PersistSession();
        };
        viewModel.Documents.CollectionChanged += (_, _) => ScheduleSessionSave();
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (_watchedDocument is { } doc && Editor.Document == doc.Document)
                doc.CaretOffset = Editor.CaretOffset;
        };
        // Files edited in another program: check whenever the user comes back to this window.
        Activated += (_, _) => _viewModel.CheckExternalChanges();

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
        SetUpExport();

        // Status bar: caret position instantly; words and line endings (whole-document scans) after a pause.
        _statsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _statsTimer.Tick += (_, _) =>
        {
            _statsTimer.Stop();
            UpdateDocumentStats();
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateCaretStatus();
        Editor.TextArea.SelectionChanged += (_, _) =>
        {
            UpdateCaretStatus();
            ScheduleStats();
        };
        Editor.DocumentChanged += (_, _) =>
        {
            UpdateCaretStatus();
            ScheduleStats();
        };
        ScheduleStats(); // the first document was bound during InitializeComponent, before these handlers

        // Drag and drop files from Explorer. Tunneling events, so the editor's own text drag-and-drop never sees
        // file drops (it would reject them) but still works for text.
        AllowDrop = true;
        PreviewDragOver += OnFileDragOver;
        PreviewDrop += OnFileDrop;
    }

    // ---------- Status bar ----------

    private readonly DispatcherTimer _statsTimer;

    private void ScheduleStats()
    {
        _statsTimer.Stop();
        _statsTimer.Start();
    }

    private void UpdateCaretStatus()
    {
        var caret = Editor.TextArea.Caret;
        var selected = Editor.SelectionLength;
        CaretStatus.Text = selected > 0
            ? $"Ln {caret.Line}, Col {caret.Column} ({selected:N0} selected)"
            : $"Ln {caret.Line}, Col {caret.Column}";
    }

    private void UpdateDocumentStats()
    {
        if (Editor.Document is not { } document) return;
        var total = TextStats.CountWords(document.Text);
        WordStatus.Text = Editor.SelectionLength > 0
            ? $"{TextStats.CountWords(Editor.SelectedText):N0} of {total:N0} words"
            : total == 1 ? "1 word" : $"{total:N0} words";
        LineEndingButton.Content = TextStats.Label(TextStats.DetectLineEnding(document));
    }

    private void OnStatusMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.DataContext = DataContext; // menus live outside the visual tree
        menu.IsOpen = true;
    }

    // ---------- Drag and drop ----------

    private static readonly string[] OpenableExtensions = [".md", ".markdown", ".mdown", ".mkd", ".txt"];

    private static bool IsOpenable(string path) =>
        OpenableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string[]? DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? paths.Where(File.Exists).ToArray()
            : null;

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        if (DroppedFiles(e) is not { } files) return; // not a file drag: leave it to the editor
        e.Effects = files.Any(f => IsOpenable(f) || MarkdownFormatting.IsImage(f)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Markdown/text files open as tabs; images become links in the active document.</summary>
    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (DroppedFiles(e) is not { } files) return;
        e.Handled = true;

        var images = files.Where(MarkdownFormatting.IsImage).ToList();
        var target = _watchedDocument; // where images go: the tab that was active when they were dropped
        if (images.Count > 0 && target is not null && Editor.Document == target.Document)
            InsertImageLinks(images, target, e);

        foreach (var path in files.Where(IsOpenable))
            _viewModel.OpenPath(path);

        Activate(); // drops from Explorer don't bring the window forward
    }

    private void InsertImageLinks(IReadOnlyList<string> images, DocumentViewModel doc, DragEventArgs e)
    {
        var folder = doc.FilePath is null ? null : Path.GetDirectoryName(doc.FilePath);
        var links = string.Join(Environment.NewLine, images.Select(path => MarkdownFormatting.ImageLink(path, folder)));

        // At the drop point when dropped on the editor text, otherwise at the caret.
        var offset = Editor.CaretOffset;
        if (EditorCard.Visibility == Visibility.Visible && Editor.GetPositionFromPoint(e.GetPosition(Editor)) is { } position)
            offset = Editor.Document.GetOffset(position.Location);

        Editor.Document.Insert(offset, links);
        Editor.CaretOffset = offset + links.Length;
        EnsureEditorVisible();
        Editor.Focus();
    }

    // ---------- Settings ----------

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.Theme))
        {
            _themeOverride = null; // an explicit choice in Settings replaces --theme
            ApplyThemeSetting();
        }
        else
        {
            ApplyEditorSettings();
        }
    }

    private void ApplyEditorSettings()
    {
        Editor.FontFamily = new System.Windows.Media.FontFamily($"{_settings.FontFamily}, Cascadia Mono, Consolas");
        Editor.FontSize = _settings.FontSize;
        Editor.WordWrap = _settings.WordWrap;
        Editor.HorizontalScrollBarVisibility = _settings.WordWrap ? System.Windows.Controls.ScrollBarVisibility.Disabled
                                                                   : System.Windows.Controls.ScrollBarVisibility.Auto;
        Editor.ShowLineNumbers = _settings.ShowLineNumbers;
    }

    /// <summary>System: follow Windows (and its changes) live. Light/Dark: fixed. Mica is kept either way.</summary>
    private void ApplyThemeSetting()
    {
        var theme = _themeOverride ?? _settings.Theme;
        if (theme == AppTheme.System)
        {
            ApplicationThemeManager.ApplySystemTheme();
            if (!_watchingSystemTheme)
            {
                SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
                _watchingSystemTheme = true;
            }
        }
        else
        {
            if (_watchingSystemTheme)
            {
                SystemThemeWatcher.UnWatch(this);
                _watchingSystemTheme = false;
            }
            ApplicationThemeManager.Apply(theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
                                          WindowBackdropType.Mica, updateAccent: true);
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new Views.SettingsWindow(_settings) { Owner = this };
        _settingsWindow.Show();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    // ---------- Editing: formatting shortcuts, list continuation, find/replace ----------

    private readonly FrontMatterColorizer _frontMatterColorizer = new();

    private void SetUpEditing()
    {
        FindBar.Attach(Editor);
        // After the .xshd highlighter in the transformer list, so its colors win inside front matter.
        Editor.TextArea.TextView.LineTransformers.Add(_frontMatterColorizer);

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

    // ---------- Export ----------

    private enum ExportKind { Html, Pdf, Clipboard }

    private readonly DialogService _exportDialogs = new();
    private DispatcherTimer? _statusTimer;
    private string? _statusOpenPath;

    private void SetUpExport()
    {
        AddKeyBinding(InputBindings, Key.E, ModifierKeys.Control | ModifierKeys.Shift, () => _ = ExportAsync(ExportKind.Html));
        AddKeyBinding(InputBindings, Key.P, ModifierKeys.Control, () => _ = ExportAsync(ExportKind.Pdf));
        AddKeyBinding(InputBindings, Key.C, ModifierKeys.Control | ModifierKeys.Shift, () => _ = ExportAsync(ExportKind.Clipboard));
        AddKeyBinding(InputBindings, Key.OemComma, ModifierKeys.Control, OpenSettings);
#if DEBUG
        // Debug builds only: throw on purpose to exercise the crash safety net in App.
        AddKeyBinding(InputBindings, Key.F12, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift,
            () => throw new InvalidOperationException("Test error from the debug shortcut (Ctrl+Alt+Shift+F12)."));
#endif
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var menu = ExportButton.ContextMenu!;
        menu.PlacementTarget = ExportButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnExportHtmlClick(object sender, RoutedEventArgs e) => _ = ExportAsync(ExportKind.Html);
    private void OnExportPdfClick(object sender, RoutedEventArgs e) => _ = ExportAsync(ExportKind.Pdf);
    private void OnCopyHtmlClick(object sender, RoutedEventArgs e) => _ = ExportAsync(ExportKind.Clipboard);

    private async Task ExportAsync(ExportKind kind)
    {
        if (_watchedDocument is not { } doc) return;
        var folder = doc.FilePath is null ? null : Path.GetDirectoryName(doc.FilePath);
        var baseName = doc.FilePath is null ? doc.DisplayName : Path.GetFileNameWithoutExtension(doc.FilePath);

        try
        {
            await Preview.InitializeAsync();
            RenderPreview(force: true); // the preview may be hidden (editor-only) and stale
            var html = await Preview.GetExportHtmlAsync(forClipboard: kind == ExportKind.Clipboard);

            switch (kind)
            {
                case ExportKind.Clipboard:
                    ExportService.CopyHtmlToClipboard(ExportService.EmbedLocalImages(html, folder), doc.Document.Text);
                    ShowStatus("Copied as HTML", openPath: null);
                    break;

                case ExportKind.Html:
                {
                    var path = _exportDialogs.ShowExportDialog(baseName + ".html", "HTML file (*.html)|*.html", ".html", folder);
                    if (path is null) return;
                    ExportService.WriteHtmlFile(path, ExportService.BuildStandaloneHtml(html, ExportService.TitleFor(html, baseName), folder));
                    ShowStatus($"Exported {Path.GetFileName(path)}", path);
                    break;
                }

                case ExportKind.Pdf:
                {
                    var path = _exportDialogs.ShowExportDialog(baseName + ".pdf", "PDF document (*.pdf)|*.pdf", ".pdf", folder);
                    if (path is null || Preview.Environment is not { } environment) return;
                    var standalone = ExportService.BuildStandaloneHtml(html, ExportService.TitleFor(html, baseName), folder);
                    await ExportService.ExportPdfAsync(environment, new System.Windows.Interop.WindowInteropHelper(this).Handle, standalone, path);
                    ShowStatus($"Exported {Path.GetFileName(path)}", path);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or WebView2RuntimeNotFoundException)
        {
            _exportDialogs.ShowError($"Export failed:\n{ex.Message}");
        }
    }

    private void ShowStatus(string message, string? openPath)
    {
        StatusText.Text = message;
        _statusOpenPath = openPath;
        StatusOpenButton.Visibility = openPath is null ? Visibility.Collapsed : Visibility.Visible;
        StatusPanel.Visibility = Visibility.Visible;

        _statusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _statusTimer.Stop();
        _statusTimer.Tick -= HideStatus;
        _statusTimer.Tick += HideStatus;
        _statusTimer.Start();
    }

    private void HideStatus(object? sender, EventArgs e)
    {
        _statusTimer?.Stop();
        StatusPanel.Visibility = Visibility.Collapsed;
    }

    private void OnStatusOpenClick(object sender, RoutedEventArgs e)
    {
        if (_statusOpenPath is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_statusOpenPath) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception) { /* no app registered for the file type */ }
        HideStatus(null, EventArgs.Empty);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyThemeSetting();
        ApplyPreviewTheme(ApplicationThemeManager.GetAppTheme());

        WatchDocument(_viewModel.ActiveDocument);
        ApplyViewMode(_viewModel.ViewMode);
        RestoreEditorPosition(_viewModel.ActiveDocument);

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
        _frontMatterColorizer.DelimiterBrush = MarkdownHighlighting.Brush("Dim", dark);
        _frontMatterColorizer.KeyBrush = MarkdownHighlighting.Brush("Link", dark);
        Editor.TextArea.TextView.Redraw();
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
        RememberEditorScroll();
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
                RestoreEditorPosition(_viewModel.ActiveDocument);
                ScheduleSessionSave();
                break;
            case nameof(MainViewModel.ViewMode):
                ApplyViewMode(_viewModel.ViewMode);
                ScheduleSessionSave();
                break;
        }
    }

    // ---------- Per-tab caret/scroll (the editor control is shared by all tabs) ----------

    private void RememberEditorScroll()
    {
        if (_positionSaveQueued) return;
        _positionSaveQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _positionSaveQueued = false;
            // Only record if the editor still shows the watched document (not mid tab-switch).
            if (_watchedDocument is { } doc && Editor.Document == doc.Document && EditorTopPosition() is var (line, _))
                doc.ScrollLine = line;
        }, DispatcherPriority.Background);
    }

    /// <summary>Puts the caret and scroll position back where they were when this tab was last shown.</summary>
    private void RestoreEditorPosition(DocumentViewModel? doc)
    {
        if (doc is null) return;
        var caret = doc.CaretOffset;
        var line = doc.ScrollLine;
        // After layout, so the editor has measured the new document.
        Dispatcher.BeginInvoke(() =>
        {
            if (Editor.Document != doc.Document) return; // switched again meanwhile
            Editor.CaretOffset = Math.Clamp(caret, 0, doc.Document.TextLength);
            ScrollEditorTo(line, line <= 0 ? "top" : null);
            doc.ScrollLine = line;
            Preview.SyncToLine(line, line <= 0 ? "top" : null);
            Editor.Focus();
        }, DispatcherPriority.Loaded);
    }

    // ---------- Session ----------

    private void ScheduleSessionSave()
    {
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    /// <summary>
    /// For the crash handlers in App: save right now, and never throw (a failure here must not become a
    /// second crash), so unlike everywhere else this catches everything.
    /// </summary>
    internal bool PersistSessionNow()
    {
        try
        {
            _sessionTimer.Stop();
            return PersistSession();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Saves tabs, unsaved text and window state. False if it couldn't be written.</summary>
    private bool PersistSession()
    {
        try
        {
            _viewModel.PersistSession(session => session with
            {
                EditorShare = CurrentEditorShare(),
                Window = CurrentPlacement(),
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private double CurrentEditorShare()
    {
        var total = EditorColumn.ActualWidth + PreviewColumn.ActualWidth;
        return _appliedMode == ViewMode.Split && total > 0 ? EditorColumn.ActualWidth / total : _editorShare;
    }

    private WindowPlacement CurrentPlacement()
    {
        // RestoreBounds is the normal-state rectangle, also while maximized or minimized.
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
    }

    private void RestorePlacement(Session? session)
    {
        if (session is null) return;
        _editorShare = Math.Clamp(session.EditorShare, 0.15, 0.85);

        if (session.Window is not { } w || w.Width < MinWidth || w.Height < MinHeight) return;
        // Only if a good part of the window would be on a screen (monitors change between sessions).
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(screen, new Rect(w.Left, w.Top, w.Width, w.Height));
        if (visible.IsEmpty || visible.Width < 200 || visible.Height < 100) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = w.Left;
        Top = w.Top;
        Width = w.Width;
        Height = w.Height;
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Called when another launch of the app hands us files (single instance).</summary>
    public void OpenFromOtherInstance(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
            _viewModel.OpenPath(path);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
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
        ScheduleSessionSave(); // backs up unsaved text shortly after typing pauses
        ScheduleStats();
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // "Save as" to another folder changes where relative images resolve from.
        if (e.PropertyName == nameof(DocumentViewModel.FilePath))
            RenderPreview();
    }

    private void RenderPreview(bool force = false)
    {
        _renderTimer.Stop();
        if ((_appliedMode == ViewMode.Editor && !force) || _watchedDocument is not { } doc) return;

        var html = MarkdownRenderer.ToHtml(doc.Document.Text);
        var folder = doc.FilePath is null ? null : Path.GetDirectoryName(doc.FilePath);
        Preview.ShowHtml(html, folder, doc.Document.LineCount);
    }

    private void ApplyViewMode(ViewMode mode)
    {
        // Remember where the user dragged the splitter before leaving split view.
        if (_appliedMode == ViewMode.Split && mode != ViewMode.Split)
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

        // Normal case: keep every tab and its unsaved text for next time; no questions asked.
        _sessionTimer.Stop();
        if (PersistSession()) return;

        // Couldn't write the session (disk full, permissions...): fall back to asking, so nothing is lost.
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
