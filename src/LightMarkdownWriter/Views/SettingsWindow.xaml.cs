using System.Reflection;
using System.Windows;
using System.Windows.Media;
using LightMarkdownWriter.Services;
using Wpf.Ui.Controls;

namespace LightMarkdownWriter.Views;

/// <summary>Settings page. Bound directly to <see cref="AppSettings"/>, so every change applies and saves immediately.</summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly FileAssociationService _associations = new();

    public SettingsWindow(AppSettings settings)
    {
        DataContext = settings;
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"LightMarkdownWriter {version?.ToString(3)}";
        UpdateAssociationStatus();
        Loaded += async (_, _) => FontCombo.ItemsSource = await Task.Run(MonospaceFonts);
    }

    /// <summary>Installed fonts where 'i' and 'W' have the same advance width.</summary>
    private static List<string> MonospaceFonts()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies)
        {
            try
            {
                var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (!typeface.TryGetGlyphTypeface(out var glyphs)) continue;
                if (!glyphs.CharacterToGlyphMap.TryGetValue('i', out var narrow) ||
                    !glyphs.CharacterToGlyphMap.TryGetValue('W', out var wide)) continue;
                if (Math.Abs(glyphs.AdvanceWidths[narrow] - glyphs.AdvanceWidths[wide]) < 0.0001)
                    names.Add(family.Source);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A broken font file shouldn't break the list.
            }
        }
        return [.. names];
    }

    private void UpdateAssociationStatus()
    {
        var state = _associations.GetState();
        AssociationStatus.Text = state switch
        {
            AssociationState.Registered => "Registered: shown in \"Open with\" for .md and .markdown files.",
            AssociationState.RegisteredElsewhere => "Registered for another copy of the app (it was moved?). Register again to use this one.",
            _ => "Not registered.",
        };
        RegisterButton.Content = state == AssociationState.NotRegistered ? "Register" : "Register again";
        UnregisterButton.IsEnabled = state != AssociationState.NotRegistered;
    }

    private void OnRegisterClick(object sender, RoutedEventArgs e) => RunRegistryAction(_associations.Register);
    private void OnUnregisterClick(object sender, RoutedEventArgs e) => RunRegistryAction(_associations.Unregister);

    private void RunRegistryAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            new DialogService().ShowError($"Could not update file associations:\n{ex.Message}");
        }
        UpdateAssociationStatus();
    }

    private void OnDefaultAppsClick(object sender, RoutedEventArgs e) => FileAssociationService.OpenDefaultAppsSettings();
}
