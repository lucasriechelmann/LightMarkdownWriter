using System.Windows;
using LightMarkdownWriter.Services;
using LightMarkdownWriter.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LightMarkdownWriter;

public partial class App : Application
{
    private SingleInstance? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Options start with "--"; everything else is a file to open.
        var files = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        // Already running? Hand the files over and exit. (If it can't be reached, start normally.)
        _singleInstance = new SingleInstance();
        if (!_singleInstance.TryBecomePrimary() && SingleInstance.SendToPrimary(files))
        {
            Shutdown();
            return;
        }

        // --theme=light|dark overrides the saved setting for this run (until changed in Settings).
        var themeOverride = e.Args
            .Select(a => a.ToLowerInvariant())
            .FirstOrDefault(a => a.StartsWith("--theme=", StringComparison.Ordinal))?["--theme=".Length..] switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            "system" => AppTheme.System,
            _ => (AppTheme?)null,
        };

        var settings = new SettingsService().Load();

        // Apply the theme before the first frame to avoid a white flash in dark mode.
        switch (themeOverride ?? settings.Theme)
        {
            case AppTheme.Light: ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, updateAccent: true); break;
            case AppTheme.Dark: ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, updateAccent: true); break;
            default: ApplicationThemeManager.ApplySystemTheme(); break;
        }

        var sessionService = new SessionService();
        var session = sessionService.Load();
        var viewModel = new MainViewModel(new DialogService(), files, sessionService, session);
        var window = new MainWindow(viewModel, settings, themeOverride, session);
        MainWindow = window;
        window.Show();

        _singleInstance.Listen(paths => Dispatcher.BeginInvoke(() => window.OpenFromOtherInstance(paths)));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
