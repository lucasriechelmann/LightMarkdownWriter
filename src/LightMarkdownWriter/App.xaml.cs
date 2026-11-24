using System.Windows;
using LightMarkdownWriter.Services;
using LightMarkdownWriter.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LightMarkdownWriter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Options start with "--"; everything else is a file to open.
        var files = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var forcedTheme = e.Args
            .Select(a => a.ToLowerInvariant())
            .FirstOrDefault(a => a.StartsWith("--theme=", StringComparison.Ordinal))?["--theme=".Length..] switch
        {
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
            _ => (ApplicationTheme?)null,
        };

        // Apply the theme before the first frame to avoid a white flash in dark mode.
        if (forcedTheme is { } theme)
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: true);
        else
            ApplicationThemeManager.ApplySystemTheme();

        var viewModel = new MainViewModel(new DialogService(), files);
        var window = new MainWindow(viewModel, followSystemTheme: forcedTheme is null);
        MainWindow = window;
        window.Show();
    }
}
