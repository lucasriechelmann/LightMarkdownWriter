using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;

namespace LightMarkdownWriter.Services;

/// <summary>
/// The taskbar/Start jump list's "Recent" category. Each entry launches the exe with the file; the single-instance
/// pipe then opens it as a tab in the running window.
/// </summary>
public static class TaskbarJumpList
{
    private const int MaxEntries = 10;

    public static void Update(IEnumerable<string> paths)
    {
        if (Environment.ProcessPath is not { } exe || Application.Current is not { } app) return;

        var list = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
        foreach (var path in paths.Take(MaxEntries))
        {
            list.JumpItems.Add(new JumpTask
            {
                Title = Path.GetFileName(path),
                Description = path,
                ApplicationPath = exe,
                Arguments = $"\"{path}\"",
                IconResourcePath = exe,
                CustomCategory = "Recent",
            });
        }

        try
        {
            JumpList.SetJumpList(app, list);
            list.Apply();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // The shell refused (e.g. no taskbar in this session): the in-app list still works.
        }
    }
}
