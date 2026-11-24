using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LightMarkdownWriter.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>User preferences. Changes apply immediately (the window listens) and are saved right away.</summary>
public partial class AppSettings : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeIndex))]
    private AppTheme _theme = AppTheme.System;

    [ObservableProperty]
    private string _fontFamily = "Cascadia Mono";

    [ObservableProperty]
    private double _fontSize = 14;

    [ObservableProperty]
    private bool _wordWrap = true;

    [ObservableProperty]
    private bool _showLineNumbers = true;

    /// <summary>For the theme ComboBox (System / Light / Dark).</summary>
    [JsonIgnore]
    public int ThemeIndex
    {
        get => (int)Theme;
        set => Theme = Enum.IsDefined((AppTheme)value) ? (AppTheme)value : AppTheme.System;
    }

    partial void OnFontSizeChanged(double value)
    {
        if (value is < 8 or > 40) FontSize = Math.Clamp(value, 8, 40);
    }

    partial void OnFontFamilyChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) FontFamily = "Cascadia Mono";
    }
}

/// <summary>Loads/saves settings.json next to the session in %LOCALAPPDATA%\LightMarkdownWriter.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightMarkdownWriter", "settings.json");

    public AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(_file)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_file), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            settings = new AppSettings(); // unreadable settings: defaults, and the next change rewrites the file
        }

        settings.PropertyChanged += (_, _) => Save(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings still apply for this run; nothing else to do.
        }
    }
}
