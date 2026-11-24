using System.Windows;
using System.Windows.Threading;
using LightMarkdownWriter.Services;
using LightMarkdownWriter.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LightMarkdownWriter;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private MainWindow? _window;

    // Crash safety net state
    private bool _errorDialogOpen;
    private long _lastErrorTick;
    private int _recentErrors;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallCrashHandlers();

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
        var recentFiles = new RecentFilesService();
        recentFiles.Changed += (_, _) => TaskbarJumpList.Update(recentFiles.Existing);
        TaskbarJumpList.Update(recentFiles.Existing);

        var viewModel = new MainViewModel(new DialogService(), files, sessionService, session, recentFiles);
        var window = new MainWindow(viewModel, settings, themeOverride, session);
        MainWindow = _window = window;
        window.Show();

        _singleInstance.Listen(paths => Dispatcher.BeginInvoke(() => window.OpenFromOtherInstance(paths)));
    }

    // ---------- Crash safety net ----------
    // The promise is "never lose text": on any unhandled error, save the session (tabs + unsaved text) first.

    private void InstallCrashHandlers()
    {
        // Sign-out, shutdown, or an installer closing the app (Restart Manager): WPF ends the session without
        // raising Window.Closing, so save explicitly here.
        SessionEnding += (_, _) => _window?.PersistSessionNow();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>UI-thread errors: save, tell the user, keep running.</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write("UI thread", e.Exception);
        var saved = _window?.PersistSessionNow() ?? false;

        // An error thrown on every render would mean an endless stream of dialogs: after a burst, let it close
        // (the session was just saved, so nothing is lost).
        var now = Environment.TickCount64;
        _recentErrors = now - _lastErrorTick < 10_000 ? _recentErrors + 1 : 1;
        _lastErrorTick = now;
        if (_recentErrors > 5) return;

        e.Handled = true;
        if (!_errorDialogOpen) ShowErrorDialog(e.Exception, saved);
    }

    /// <summary>Background-thread errors always terminate the process: just save what we can first.</summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) CrashLog.Write("background thread", ex);
        try
        {
            Dispatcher.Invoke(() => _window?.PersistSessionNow(), TimeSpan.FromSeconds(3));
        }
        catch (Exception saveError) when (saveError is TimeoutException or InvalidOperationException or TaskCanceledException)
        {
            // The UI thread is stuck or gone; the last periodic save (≤ 2 s old) is what survives.
        }
    }

    private async void ShowErrorDialog(Exception exception, bool saved)
    {
        _errorDialogOpen = true;
        try
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = "LightMarkdownWriter",
                Content = $"Something went wrong: {exception.Message}\n\n"
                          + (saved ? "Your tabs and unsaved text are saved. " : "Your tabs couldn't be saved just now. ")
                          + $"You can keep working.\n\nDetails were written to:\n{CrashLog.FilePath}",
                CloseButtonText = "OK",
                Owner = _window,
            };
            await box.ShowDialogAsync();
        }
        finally
        {
            _errorDialogOpen = false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
