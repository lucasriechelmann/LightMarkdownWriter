using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using LightMarkdownWriter.Editing;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace LightMarkdownWriter.Views;

/// <summary>VS Code-style find/replace overlay for the editor: match case, whole word, regex, highlight all.</summary>
public partial class FindReplaceBar : UserControl
{
    private const int MaxMatches = 10_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly SearchResultRenderer _renderer = new();
    private readonly DispatcherTimer _refreshTimer;
    private TextEditor? _editor;
    private Regex? _regex;
    private List<(int Offset, int Length)> _matches = [];

    public FindReplaceBar()
    {
        InitializeComponent();
        // Re-search shortly after the document changes rather than on every keystroke.
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Refresh();
        };
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    // VS Code's option shortcuts while the bar has focus: Alt+C case, Alt+W whole word, Alt+R regex.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (Keyboard.Modifiers != ModifierKeys.Alt) return;

        var toggle = (e.Key == Key.System ? e.SystemKey : e.Key) switch
        {
            Key.C => CaseToggle,
            Key.W => WordToggle,
            Key.R => RegexToggle,
            _ => null,
        };
        if (toggle is null) return;

        toggle.IsChecked = toggle.IsChecked != true;
        Refresh();
        e.Handled = true;
    }

    public void Attach(TextEditor editor)
    {
        _editor = editor;
        editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        editor.TextChanged += (_, _) => ScheduleRefresh();
        editor.DocumentChanged += (_, _) => ScheduleRefresh(); // switched tabs
        editor.TextArea.SelectionChanged += (_, _) => { if (IsOpen) UpdateInfo(); };
    }

    public void Open(bool showReplace)
    {
        if (_editor is null) return;
        Visibility = Visibility.Visible;
        SetReplaceVisible(showReplace || ReplaceRow.Visibility == Visibility.Visible);

        // Seed the query with a single-line selection, like most editors.
        var selected = _editor.SelectedText;
        if (selected.Length > 0 && !selected.Contains('\n'))
            FindBox.Text = selected;

        Refresh();
        var box = showReplace && FindBox.Text.Length > 0 ? ReplaceBox : FindBox;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _matches = [];
        _renderer.Matches = _matches;
        _editor?.TextArea.TextView.InvalidateLayer(_renderer.Layer);
        _editor?.Focus();
    }

    // ---------- Searching ----------

    private void ScheduleRefresh()
    {
        if (!IsOpen) return;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void Refresh()
    {
        if (_editor?.Document is not { } doc) return;
        _regex = BuildRegex(out var error);
        _matches = [];

        if (_regex is not null)
        {
            try
            {
                for (var match = _regex.Match(doc.Text); match.Success && _matches.Count < MaxMatches; match = match.NextMatch())
                {
                    if (match.Length > 0) _matches.Add((match.Index, match.Length)); // empty matches can't be selected
                }
            }
            catch (RegexMatchTimeoutException)
            {
                error = "Search too slow";
                _matches = [];
            }
        }

        _renderer.Matches = _matches;
        _editor.TextArea.TextView.InvalidateLayer(_renderer.Layer);
        UpdateInfo(error);
    }

    private Regex? BuildRegex(out string? error)
    {
        error = null;
        var query = FindBox.Text;
        if (query.Length == 0) return null;

        var pattern = RegexToggle.IsChecked == true ? query : Regex.Escape(query);
        if (WordToggle.IsChecked == true) pattern = $@"\b(?:{pattern})\b";
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (CaseToggle.IsChecked != true) options |= RegexOptions.IgnoreCase;

        try
        {
            return new Regex(pattern, options, RegexTimeout);
        }
        catch (ArgumentException)
        {
            error = "Invalid regex";
            return null;
        }
    }

    private void UpdateInfo(string? error = null)
    {
        if (error is not null)
        {
            MatchInfo.Text = error;
            MatchInfo.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            return;
        }

        MatchInfo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var current = CurrentMatchIndex();
        MatchInfo.Text = _matches.Count switch
        {
            0 when FindBox.Text.Length == 0 => "",
            0 => "No results",
            >= MaxMatches => current >= 0 ? $"{current + 1} of {MaxMatches}+" : $"{MaxMatches}+ results",
            _ => current >= 0 ? $"{current + 1} of {_matches.Count}" : $"{_matches.Count} results",
        };
    }

    /// <summary>Index of the match the selection currently covers, or -1.</summary>
    private int CurrentMatchIndex()
    {
        if (_editor is null) return -1;
        var index = _matches.BinarySearch((_editor.SelectionStart, 0), Comparer<(int Offset, int Length)>.Create((a, b) => a.Offset.CompareTo(b.Offset)));
        return index >= 0 && _matches[index].Length == _editor.SelectionLength ? index : -1;
    }

    public void FindNext(bool forward = true)
    {
        if (_editor is null) return;
        if (!IsOpen) { Open(showReplace: false); return; }
        if (_matches.Count == 0) return;

        int index;
        if (forward)
        {
            var from = _editor.SelectionStart + _editor.SelectionLength;
            index = _matches.FindIndex(m => m.Offset >= from);
            if (index < 0) index = 0; // wrap to the top
        }
        else
        {
            var from = _editor.SelectionStart;
            index = _matches.FindLastIndex(m => m.Offset + m.Length <= from && m.Offset < from);
            if (index < 0) index = _matches.Count - 1; // wrap to the bottom
        }

        var (offset, length) = _matches[index];
        _editor.Select(offset, length);
        var location = _editor.Document.GetLocation(offset);
        _editor.ScrollTo(location.Line, location.Column);
        UpdateInfo();
    }

    // ---------- Replacing ----------

    private string ReplacementFor(Match match) =>
        RegexToggle.IsChecked == true ? match.Result(ReplaceBox.Text) : ReplaceBox.Text;

    private void ReplaceCurrent()
    {
        if (_editor?.Document is not { } doc || _regex is null) return;

        // Replace only if the selection is exactly a match; either way, move on to the next one.
        var start = _editor.SelectionStart;
        var match = _regex.Match(doc.Text, start);
        if (match.Success && match.Index == start && match.Length == _editor.SelectionLength && match.Length > 0)
        {
            var replacement = ReplacementFor(match);
            doc.Replace(start, match.Length, replacement);
            _editor.Select(start + replacement.Length, 0);
            Refresh();
        }
        FindNext(forward: true);
    }

    private void ReplaceAll()
    {
        if (_editor?.Document is not { } doc || _regex is null) return;

        var matches = _regex.Matches(doc.Text).Cast<Match>().ToList();
        using (doc.RunUpdate()) // one undo step
        {
            for (var i = matches.Count - 1; i >= 0; i--) // back to front so earlier offsets stay valid
                doc.Replace(matches[i].Index, matches[i].Length, ReplacementFor(matches[i]));
        }
        Refresh();
        _refreshTimer.Stop(); // our own edits scheduled a refresh; it would overwrite this message
        MatchInfo.Text = $"Replaced {matches.Count}";
    }

    // ---------- UI events ----------

    private void SetReplaceVisible(bool visible)
    {
        ReplaceRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.Icon = new SymbolIcon(visible ? SymbolRegular.ChevronDown20 : SymbolRegular.ChevronRight20);
    }

    private void OnExpandClick(object sender, RoutedEventArgs e) =>
        SetReplaceVisible(ReplaceRow.Visibility != Visibility.Visible);

    private void OnQueryChanged(object sender, RoutedEventArgs e)
    {
        if (!IsOpen) return;
        Refresh();
        // Like VS Code: typing jumps to the first match at/after the caret.
        if (sender == FindBox && _editor is not null && _matches.Count > 0)
        {
            _editor.Select(_editor.SelectionStart, 0);
            FindNext(forward: true);
        }
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                FindNext(forward: Keyboard.Modifiers != ModifierKeys.Shift);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void OnReplaceBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt):
                ReplaceAll();
                e.Handled = true;
                break;
            case Key.Enter:
                ReplaceCurrent();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => FindNext(forward: false);
    private void OnNextClick(object sender, RoutedEventArgs e) => FindNext(forward: true);
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnReplaceClick(object sender, RoutedEventArgs e) => ReplaceCurrent();
    private void OnReplaceAllClick(object sender, RoutedEventArgs e) => ReplaceAll();
}
