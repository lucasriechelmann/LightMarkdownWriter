using System.Windows;
using Microsoft.Win32;

namespace LightMarkdownWriter.Services;

public enum SaveChoice { Save, Discard, Cancel }

public interface IDialogService
{
    string[] ShowOpenDialog();
    string? ShowSaveDialog(string suggestedFileName);
    Task<SaveChoice> AskSaveChangesAsync(string documentName);
    void ShowError(string message);
}

public sealed class DialogService : IDialogService
{
    private const string Filter =
        "Markdown files (*.md;*.markdown)|*.md;*.markdown;*.mdown;*.mkd|Text files (*.txt)|*.txt|All files (*.*)|*.*";

    public string[] ShowOpenDialog()
    {
        var dialog = new OpenFileDialog { Filter = Filter, Multiselect = true };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileNames : [];
    }

    public string? ShowSaveDialog(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = Filter,
            FileName = suggestedFileName,
            DefaultExt = ".md",
            AddExtension = true,
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public async Task<SaveChoice> AskSaveChangesAsync(string documentName)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "LightMarkdownWriter",
            Content = $"Do you want to save changes to {documentName}?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            Owner = Application.Current.MainWindow,
        };

        return await box.ShowDialogAsync() switch
        {
            Wpf.Ui.Controls.MessageBoxResult.Primary => SaveChoice.Save,
            Wpf.Ui.Controls.MessageBoxResult.Secondary => SaveChoice.Discard,
            _ => SaveChoice.Cancel,
        };
    }

    public void ShowError(string message)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "LightMarkdownWriter",
            Content = message,
            CloseButtonText = "OK",
            Owner = Application.Current.MainWindow,
        };
        _ = box.ShowDialogAsync();
    }
}
