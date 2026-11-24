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
        Preview.MarkdownFileLinkClicked += OpenAtHeading;
        Preview.SourceLineRequested += line =>
        {
            // Double-click in the preview: show the editor if needed, then put the caret on that source line.
            EnsureEditorVisible();
            Dispatcher.BeginInvoke(() => NavigateToLine(line + 1, 1, alignTop: false), DispatcherPriority.ContextIdle);
        };
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
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            UpdateCaretStatus();
            SelectCurrentHeading();
        };
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

    // ---------- Recent files menu (9.1, 9.2) ----------

    /// <summary>Built on each open so it lists only files that still exist, newest first.</summary>
    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        var menu = RecentButton.ContextMenu!;
        menu.Items.Clear();

        var recent = _viewModel.RecentFiles?.Existing ?? [];
        if (recent.Count == 0)
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "No recent files", IsEnabled = false });

        foreach (var path in recent)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem
            {
                // Underscores would become access keys in a menu header, so escape them.
                Header = Path.GetFileName(path).Replace("_", "__"),
                ToolTip = path,
                InputGestureText = ShortenFolder(Path.GetDirectoryName(path)),
                Command = _viewModel.OpenRecentCommand,
                CommandParameter = path,
            });
        }

        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "Reopen closed tab",
            InputGestureText = "Ctrl+Shift+T",
            Command = _viewModel.ReopenClosedTabCommand,
            IsEnabled = _viewModel.CanReopenClosedTab,
        });
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "Clear recent files",
            Command = _viewModel.ClearRecentCommand,
            IsEnabled = recent.Count > 0,
        });

        menu.PlacementTarget = RecentButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>"C:\Users\me\Documents\notes" → "…\Documents\notes" (the last two folders).</summary>
    private static string ShortenFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return "";
        var parts = folder.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 3 ? folder : "…\\" + string.Join('\\', parts[^2..]);
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

    private int _statsGeneration;

    /// <summary>Word count and line endings scan the whole text: on a background thread for big documents (12.2).</summary>
    private async void UpdateDocumentStats()
    {
        if (Editor.Document is not { } document) return;
        var text = document.Text;
        var selected = Editor.SelectionLength > 0 ? Editor.SelectedText : null;
        var generation = ++_statsGeneration;

        (int Total, int? Selected, LineEnding Ending) Scan() =>
            (TextStats.CountWords(text), selected is null ? null : TextStats.CountWords(selected), TextStats.DetectLineEnding(text));
        var stats = text.Length < BackgroundWorkThreshold ? Scan() : await Task.Run(Scan);
        if (generation != _statsGeneration || Editor.Document != document) return; // a newer scan is on its way

        WordStatus.Text = stats.Selected is { } inSelection
            ? $"{inSelection:N0} of {stats.Total:N0} words"
            : stats.Total == 1 ? "1 word" : $"{stats.Total:N0} words";
        var lineEndings = TextStats.Label(stats.Ending);
        LineEndingButton.Content = lineEndings;
        // Screen readers read the name instead of the text, so the name carries the value too.
        System.Windows.Automation.AutomationProperties.SetName(LineEndingButton, $"Line endings: {lineEndings}");
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

    // ---------- Paste images (11.6) ----------

    private void SetUpImagePaste()
    {
        // AvalonEdit only knows how to paste text; these run first and take over when the clipboard holds an image.
        CommandManager.AddPreviewCanExecuteHandler(Editor.TextArea, (_, e) =>
        {
            if (e.Command == ApplicationCommands.Paste && ClipboardHasImage()) { e.CanExecute = true; e.Handled = true; }
        });
        CommandManager.AddPreviewExecutedHandler(Editor.TextArea, (_, e) =>
        {
            if (e.Command != ApplicationCommands.Paste || !ClipboardHasImage()) return;
            e.Handled = true;
            _ = PasteImageAsync();
        });
    }

    /// <summary>Text on the clipboard always wins (Word and browsers put text and a picture there together).</summary>
    private static bool ClipboardHasImage()
    {
        try
        {
            return !Clipboard.ContainsText() && (Clipboard.ContainsImage() || ClipboardImageFiles().Count > 0);
        }
        catch (System.Runtime.InteropServices.ExternalException) { return false; } // clipboard busy
    }

    /// <summary>Image files copied in File Explorer (Ctrl+C on the files).</summary>
    private static List<string> ClipboardImageFiles() =>
        Clipboard.ContainsFileDropList()
            ? Clipboard.GetFileDropList().Cast<string>().Where(p => File.Exists(p) && MarkdownFormatting.IsImage(p)).ToList()
            : [];

    private async Task PasteImageAsync()
    {
        if (_viewModel.ActiveDocument is not { } doc) return;

        // Copied image files are linked where they are, like dropping them.
        if (ClipboardImageFiles() is { Count: > 0 } files)
        {
            var folder = doc.FilePath is null ? null : Path.GetDirectoryName(doc.FilePath);
            InsertAtCaret(string.Join(Environment.NewLine, files.Select(f => MarkdownFormatting.ImageLink(f, folder))));
            return;
        }

        // A picture (a screenshot, say) has to be written somewhere first: next to the document.
        if (doc.FilePath is null)
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Paste image",
                Content = "Save this document first. Pasted images are stored next to it, in an images folder.",
                PrimaryButtonText = "Save as…",
                CloseButtonText = "Cancel",
            };
            if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
            _viewModel.SaveAsCommand.Execute(null);
            if (doc.FilePath is null || _viewModel.ActiveDocument != doc) return; // Save as was cancelled
        }

        var documentFolder = Path.GetDirectoryName(doc.FilePath)!;
        var path = MarkdownFormatting.PastedImagePath(documentFolder, DateTime.Now);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!WriteClipboardPng(path)) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            new DialogService().ShowError($"Could not save the pasted image to {path}:\n{ex.Message}");
            return;
        }

        // Undo removes the link; the image file stays (deleting files on undo could lose a picture used elsewhere).
        InsertAtCaret(MarkdownFormatting.ImageLink(path, documentFolder));
        ShowStatus($"Image saved as {Path.GetRelativePath(documentFolder, path).Replace('\\', '/')}", openPath: path);
    }

    /// <summary>
    /// Writes the clipboard picture as a PNG. Screenshot tools also put a "PNG" stream there, which keeps
    /// transparency exactly, so that's preferred over the bitmap (CF_DIB) that <see cref="Clipboard.GetImage"/> reads.
    /// </summary>
    private static bool WriteClipboardPng(string path)
    {
        if (Clipboard.GetData("PNG") is MemoryStream png)
        {
            using var file = File.Create(path);
            png.CopyTo(file);
            return true;
        }
        if (Clipboard.GetImage() is not { } bitmap) return false;
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        return true;
    }

    private void InsertAtCaret(string text)
    {
        var start = Editor.SelectionStart;
        Editor.Document.Replace(start, Editor.SelectionLength, text);
        Editor.Select(start + text.Length, 0);
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
        else if (e.PropertyName == nameof(AppSettings.ShowOutline))
        {
            ApplyOutlineVisibility();
        }
        else if (e.PropertyName == nameof(AppSettings.CheckForUpdates))
        {
            _ = CheckForUpdatesAsync(); // turned on: check now; turned off: hide the button
        }
        else if (e.PropertyName is nameof(AppSettings.LastUpdateCheckUtc) or nameof(AppSettings.LatestReleaseTag)
                 or nameof(AppSettings.LatestReleaseUrl))
        {
            // Bookkeeping of the update check, not something to apply.
        }
        else
        {
            ApplyEditorSettings();
        }
    }

    // ---------- Update check (13.3) ----------

    private string? _updateUrl;

    private async Task CheckForUpdatesAsync()
    {
        var update = await UpdateService.CheckAsync(_settings, UpdateService.CurrentVersion);
        _updateUrl = update?.Url;
        UpdateButton.Content = update is null ? null : $"Version {update.Version} available";
        System.Windows.Automation.AutomationProperties.SetName(UpdateButton, update is null ? "" : $"Version {update.Version} available, open download page");
        UpdateButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        // Only ever a github.com release page (UpdateService checks the URL it got from the API).
        if (_updateUrl is not null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_updateUrl) { UseShellExecute = true });
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
        AutoPairs.Attach(Editor, () => _settings.AutoClosePairs);
        SetUpImagePaste();

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
        AddKeyBinding(editorBindings, Key.F, ModifierKeys.Control | ModifierKeys.Shift, () => TableFormatter.FormatOrInsert(Editor));
        AddKeyBinding(editorBindings, Key.Enter, ModifierKeys.Control | ModifierKeys.Shift, () => TableFormatter.AddRow(Editor));

        Editor.TextArea.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
                e.Handled = MarkdownFormatting.ContinueListOnEnter(Editor);
            else if (e.Key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
                // On list lines Tab/Shift+Tab nest and un-nest items; elsewhere AvalonEdit's own Tab runs.
                e.Handled = MarkdownFormatting.ShiftListItems(Editor, outdent: Keyboard.Modifiers == ModifierKeys.Shift);
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
    private void OnTableClick(object sender, RoutedEventArgs e) => RunFormatting(ed => TableFormatter.FormatOrInsert(ed));
    private void OnFindClick(object sender, RoutedEventArgs e) => OpenFind(replace: false);

    // ---------- Tab strip: drag to reorder, context menu (11.4) ----------

    private DocumentViewModel? _draggedTab;
    private Point _dragStart;

    /// <summary>The tab (its document) under the mouse, if any.</summary>
    private DocumentViewModel? TabAt(MouseEventArgs e) =>
        TabStrip.InputHitTest(e.GetPosition(TabStrip)) is DependencyObject hit
        && System.Windows.Controls.ItemsControl.ContainerFromElement(TabStrip, hit) is System.Windows.Controls.TabItem { DataContext: DocumentViewModel doc } ? doc : null;

    private void OnTabStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Not handled: the click still selects the tab (or presses its close button).
        _draggedTab = TabAt(e);
        _dragStart = e.GetPosition(TabStrip);
    }

    private void OnTabStripMouseUp(object sender, MouseButtonEventArgs e) => _draggedTab = null;

    /// <summary>
    /// Reorders live while dragging, like a browser. No OLE drag-and-drop: the window already handles file drops,
    /// and moving the tab under the mouse needs no data transfer.
    /// </summary>
    private void OnTabStripMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedTab is not { } dragged || e.LeftButton != MouseButtonState.Pressed)
        {
            _draggedTab = null;
            return;
        }
        var position = e.GetPosition(TabStrip);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        if (TabAt(e) is not { } target || target == dragged) return;

        // Swap only once the mouse passes the middle of the tab it's over. Tabs differ in width, so swapping as soon
        // as the mouse touches a neighbour would make it land under the mouse again and swap straight back.
        var targetIndex = _viewModel.Documents.IndexOf(target);
        if (TabStrip.ItemContainerGenerator.ContainerFromItem(target) is not System.Windows.Controls.TabItem container) return;
        var middle = container.TranslatePoint(new Point(container.ActualWidth / 2, 0), TabStrip).X;
        var movingRight = targetIndex > _viewModel.Documents.IndexOf(dragged);
        if (movingRight ? position.X < middle : position.X > middle) return;

        _viewModel.MoveTab(dragged, targetIndex);
    }

    private static DocumentViewModel? MenuDocument(object sender) =>
        (sender as FrameworkElement)?.DataContext as DocumentViewModel;

    private void OnTabMenuOpened(object sender, RoutedEventArgs e)
    {
        var hasPath = MenuDocument(sender)?.FilePath is not null;
        foreach (var item in ((System.Windows.Controls.ContextMenu)sender).Items.OfType<System.Windows.Controls.MenuItem>())
            if (item.Name is "CopyPathItem" or "RevealItem") item.IsEnabled = hasPath;
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (MenuDocument(sender)?.FilePath is not { } path) return;
        try { Clipboard.SetText(path); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy in another app; the user can retry */ }
    }

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        if (MenuDocument(sender)?.FilePath is not { } path) return;
        // explorer.exe /select opens the folder with the file highlighted (or just the folder if the file is gone).
        System.Diagnostics.Process.Start("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"");
    }

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
        AddKeyBinding(InputBindings, Key.G, ModifierKeys.Control, OpenGoToLine);
        AddKeyBinding(InputBindings, Key.O, ModifierKeys.Control | ModifierKeys.Shift, ToggleOutline);

        // Zoom (11.5): the editor font size. Both the main-keyboard and the numpad keys; Ctrl+Shift+= is Ctrl++.
        foreach (var (key, modifiers, step) in new[]
                 {
                     (Key.OemPlus, ModifierKeys.Control, 1), (Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift, 1),
                     (Key.Add, ModifierKeys.Control, 1), (Key.OemMinus, ModifierKeys.Control, -1),
                     (Key.Subtract, ModifierKeys.Control, -1), (Key.D0, ModifierKeys.Control, 0), (Key.NumPad0, ModifierKeys.Control, 0),
                 })
            AddKeyBinding(InputBindings, key, modifiers, () => Zoom(step));
        // Ctrl+wheel over the editor only; over the preview WebView2 keeps its own page zoom.
        Editor.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            Zoom(Math.Sign(e.Delta));
            e.Handled = true;
        };
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

    /// <summary>+1 / -1 changes the editor font size by a point; 0 resets it. Saved like any other setting.</summary>
    private void Zoom(int step)
    {
        _settings.FontSize = step == 0 ? AppSettings.DefaultFontSize : _settings.FontSize + step;
        var percent = (int)Math.Round(_settings.FontSize / AppSettings.DefaultFontSize * 100);
        ShowStatus($"Editor zoom {percent}% ({_settings.FontSize:0} pt)", openPath: null);
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
        ApplyOutlineVisibility();

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

        await CheckForUpdatesAsync(); // after the window and preview are up; fails silently when offline
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

    /// <summary>
    /// Documents up to this many characters render synchronously on the UI thread (about 20 ms at this size);
    /// bigger ones render on a background thread so typing never waits for Markdig (12.2: 1 MB took ~190 ms,
    /// 5 MB ~930 ms). Measured numbers are in PLAN.md.
    /// </summary>
    private const int BackgroundWorkThreshold = 100_000;

    /// <summary>Wait longer after typing in big documents, so a render doesn't start on every short pause.</summary>
    private static TimeSpan RenderDelay(int length) => length switch
    {
        < BackgroundWorkThreshold => TimeSpan.FromMilliseconds(150),
        < 1_000_000 => TimeSpan.FromMilliseconds(400),
        _ => TimeSpan.FromMilliseconds(800),
    };

    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        _renderTimer.Stop();
        _renderTimer.Interval = RenderDelay(_watchedDocument?.Document.TextLength ?? 0);
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

    private int _renderGeneration;

    /// <summary>
    /// One parse feeds both the preview and the outline; skipped when neither is visible. Big documents are parsed
    /// on a background thread from a snapshot of the text; if the text or tab changed meanwhile, the result is
    /// dropped, because a newer render is already scheduled.
    /// </summary>
    private async void RenderPreview(bool force = false)
    {
        _renderTimer.Stop();
        var previewVisible = _appliedMode != ViewMode.Editor || force;
        if (_watchedDocument is not { } doc || (!previewVisible && !_settings.ShowOutline)) return;

        var text = doc.Document.Text; // TextDocument belongs to the UI thread: snapshot it here
        var lineCount = doc.Document.LineCount;
        var generation = ++_renderGeneration;
        var result = text.Length < BackgroundWorkThreshold
            ? MarkdownRenderer.Render(text)
            : await Task.Run(() => MarkdownRenderer.Render(text)); // Markdig pipelines are thread-safe
        if (generation != _renderGeneration || _watchedDocument != doc) return;

        if (_settings.ShowOutline) UpdateOutline(result.Headings);
        if (!previewVisible) return;

        var folder = doc.FilePath is null ? null : Path.GetDirectoryName(doc.FilePath);
        Preview.ShowHtml(result.Html, folder, lineCount);
    }

    // ---------- Outline (9.4) and Go to line (9.3) ----------

    // Navigation happens on click/Enter (not SelectionChanged), so the code can move the selection freely.
    private IReadOnlyList<HeadingInfo> _headings = [];

    private void ApplyOutlineVisibility()
    {
        var show = _settings.ShowOutline;
        OutlineCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OutlineButton.Appearance = show ? ControlAppearance.Secondary : ControlAppearance.Transparent;
        if (show) RenderPreview(); // fill it now, even in editor-only mode
    }

    private void ToggleOutline() => _settings.ShowOutline = !_settings.ShowOutline;

    private void OnOutlineToggleClick(object sender, RoutedEventArgs e) => ToggleOutline();

    private void UpdateOutline(IReadOnlyList<HeadingInfo> headings)
    {
        _headings = headings;
        OutlineList.ItemsSource = headings;
        OutlineEmpty.Visibility = headings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectCurrentHeading();
    }

    /// <summary>Highlights the section the caret is in, like VS Code's outline.</summary>
    private void SelectCurrentHeading()
    {
        if (!_settings.ShowOutline || _headings.Count == 0) return;
        var caretLine = Editor.TextArea.Caret.Line - 1; // headings use 0-based lines
        var current = _headings.LastOrDefault(h => h.Line <= caretLine);
        if (ReferenceEquals(OutlineList.SelectedItem, current)) return;
        OutlineList.SelectedItem = current;
        if (current is not null) OutlineList.ScrollIntoView(current);
    }

    private void OnOutlineClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is HeadingInfo heading)
            NavigateToLine(heading.Line + 1, 1, alignTop: true);
    }

    private void OnOutlineKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || OutlineList.SelectedItem is not HeadingInfo heading) return;
        NavigateToLine(heading.Line + 1, 1, alignTop: true);
        e.Handled = true;
    }

    /// <summary>
    /// Puts the caret on a line and scrolls both panes there: the heading at the top for the outline,
    /// the line centred for Go to line. In split view the preview follows the editor through scroll sync;
    /// in preview-only view it's scrolled directly.
    /// </summary>
    private void NavigateToLine(int line, int column, bool alignTop)
    {
        if (Editor.Document is not { } document) return;
        line = Math.Clamp(line, 1, document.LineCount);
        var target = document.GetLineByNumber(line);
        Editor.CaretOffset = target.Offset + Math.Clamp(column - 1, 0, target.Length);

        var view = Editor.TextArea.TextView;
        var top = view.GetVisualTopByDocumentLine(line);
        Editor.ScrollToVerticalOffset(Math.Max(0, alignTop ? top : top - view.ActualHeight / 2));

        if (alignTop || _appliedMode == ViewMode.Preview)
            Preview.SyncToLine(line - 1, line == 1 ? "top" : null);
        if (EditorCard.Visibility == Visibility.Visible)
            Dispatcher.BeginInvoke(() => Editor.Focus(), DispatcherPriority.Input);
    }

    /// <summary>
    /// A link like <c>other.md#setup</c>: open (or switch to) the file, then put that heading at the top of both
    /// panes. Runs after the tab switch has restored its own caret/scroll (ContextIdle comes after Loaded).
    /// </summary>
    private void OpenAtHeading(string path, string? headingId)
    {
        _viewModel.OpenPath(path);
        if (string.IsNullOrEmpty(headingId)) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (_viewModel.ActiveDocument is not { } doc ||
                !string.Equals(doc.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return;
            var heading = MarkdownRenderer.Render(doc.Document.Text).Headings
                .FirstOrDefault(h => string.Equals(h.Id, headingId, StringComparison.OrdinalIgnoreCase));
            if (heading is not null) NavigateToLine(heading.Line + 1, 1, alignTop: true);
        }, DispatcherPriority.ContextIdle);
    }

    private void OpenGoToLine()
    {
        if (Editor.Document is not { } document) return;
        StatusPanel.Visibility = Visibility.Collapsed;
        GoToPanel.Visibility = Visibility.Visible;
        GoToBox.PlaceholderText = $"Go to line (1–{document.LineCount:N0}) or line:column";
        GoToBox.Text = "";
        Dispatcher.BeginInvoke(() => GoToBox.Focus(), DispatcherPriority.Input);
    }

    private void CloseGoToLine(bool focusEditor)
    {
        if (GoToPanel.Visibility != Visibility.Visible) return;
        GoToPanel.Visibility = Visibility.Collapsed;
        if (focusEditor && EditorCard.Visibility == Visibility.Visible) Editor.Focus();
    }

    private void OnGoToKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseGoToLine(focusEditor: true);
                e.Handled = true;
                break;
            case Key.Enter:
                if (TextStats.TryParseLineColumn(GoToBox.Text, out var line, out var column))
                {
                    CloseGoToLine(focusEditor: false);
                    NavigateToLine(line, column, alignTop: false);
                }
                e.Handled = true; // invalid input: stay open so it can be corrected
                break;
        }
    }

    private void OnGoToLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CloseGoToLine(focusEditor: false);

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
