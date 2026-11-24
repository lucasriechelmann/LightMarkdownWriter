using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LightMarkdownWriter.Services;

public enum AssociationState { NotRegistered, Registered, RegisteredElsewhere }

/// <summary>
/// Registers the app for .md/.markdown under HKCU (no admin rights): a ProgId, "Open with" entries, and
/// Capabilities so it appears in Settings → Default apps. Windows 11 doesn't let apps make themselves the
/// default, so the user picks it there (<see cref="OpenDefaultAppsSettings"/>).
/// </summary>
public sealed class FileAssociationService
{
    public const string ProgId = "LightMarkdownWriter.md";
    public const string AppName = "LightMarkdownWriter";
    private static readonly string[] Extensions = [".md", ".markdown"];

    private readonly RegistryKey _softwareRoot; // HKCU\Software, or a scratch key in tests
    private readonly string _exePath;
    private readonly bool _notifyShell;

    private readonly bool _isRealRoot; // false in tests (scratch key): never read the real machine/user state

    public FileAssociationService(RegistryKey? softwareRoot = null, string? exePath = null, bool notifyShell = true)
    {
        _isRealRoot = softwareRoot is null;
        _softwareRoot = softwareRoot ?? Registry.CurrentUser.CreateSubKey("Software");
        _exePath = exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        _notifyShell = notifyShell;
    }

    private string OpenCommand => $"\"{_exePath}\" \"%1\"";
    private string ExeName => Path.GetFileName(_exePath);
    private string CapabilitiesPath => $@"Software\{AppName}\Capabilities";

    public AssociationState GetState()
    {
        using var command = _softwareRoot.OpenSubKey($@"Classes\{ProgId}\shell\open\command");
        return command?.GetValue("") is not string registered
            ? AssociationState.NotRegistered
            : string.Equals(registered, OpenCommand, StringComparison.OrdinalIgnoreCase)
                ? AssociationState.Registered
                : AssociationState.RegisteredElsewhere; // e.g. the exe was moved since registering
    }

    public void Register()
    {
        using var classes = _softwareRoot.CreateSubKey("Classes");

        using (var progId = classes.CreateSubKey(ProgId))
        {
            progId.SetValue("", "Markdown document");
            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{_exePath}\",0");
            using var command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue("", OpenCommand);
        }

        foreach (var extension in Extensions)
        {
            // "Open with" list entry.
            using var openWith = classes.CreateSubKey($@"{extension}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);

            // Default for double-click, but only when nothing else is the default: never take over another
            // app's association. (A user's explicit choice in Windows — UserChoice — wins over this anyway.)
            using var extensionKey = classes.CreateSubKey(extension);
            if (string.IsNullOrEmpty(extensionKey.GetValue("") as string) && !HasMachineDefault(extension))
                extensionKey.SetValue("", ProgId);
        }

        using (var application = classes.CreateSubKey($@"Applications\{ExeName}"))
        {
            application.SetValue("FriendlyAppName", AppName);
            using var command = application.CreateSubKey(@"shell\open\command");
            command.SetValue("", OpenCommand);
            using var supported = application.CreateSubKey("SupportedTypes");
            foreach (var extension in Extensions) supported.SetValue(extension, "");
        }

        // Capabilities + RegisteredApplications make the app show up in Settings → Default apps.
        using (var capabilities = _softwareRoot.CreateSubKey($@"{AppName}\Capabilities"))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "A light markdown editor with live preview.");
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (var extension in Extensions) associations.SetValue(extension, ProgId);
        }
        using (var registered = _softwareRoot.CreateSubKey("RegisteredApplications"))
            registered.SetValue(AppName, CapabilitiesPath);

        NotifyShell();
    }

    public void Unregister()
    {
        using (var classes = _softwareRoot.OpenSubKey("Classes", writable: true))
        {
            if (classes is not null)
            {
                classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
                classes.DeleteSubKeyTree($@"Applications\{ExeName}", throwOnMissingSubKey: false);
                foreach (var extension in Extensions)
                {
                    using var openWith = classes.OpenSubKey($@"{extension}\OpenWithProgids", writable: true);
                    openWith?.DeleteValue(ProgId, throwOnMissingValue: false);

                    // Remove the default only if it's still ours.
                    using var extensionKey = classes.OpenSubKey(extension, writable: true);
                    if (extensionKey?.GetValue("") as string == ProgId)
                        extensionKey.DeleteValue("", throwOnMissingValue: false);
                }
            }
        }

        using (var app = _softwareRoot.OpenSubKey(AppName, writable: true))
            app?.DeleteSubKeyTree("Capabilities", throwOnMissingSubKey: false);
        using (var registered = _softwareRoot.OpenSubKey("RegisteredApplications", writable: true))
            registered?.DeleteValue(AppName, throwOnMissingValue: false);

        NotifyShell();
    }

    /// <summary>
    /// True when double-clicking a .md file opens this app: either the user picked it in Windows (UserChoice),
    /// or there's no UserChoice and the per-user default is our ProgId.
    /// </summary>
    public bool IsDefaultForMarkdown()
    {
        if (_isRealRoot)
        {
            using var choice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\UserChoice");
            if (choice?.GetValue("ProgId") is string chosen)
                return chosen == ProgId || chosen.EndsWith(ExeName, StringComparison.OrdinalIgnoreCase);
        }
        using var extension = _softwareRoot.OpenSubKey(@"Classes\.md");
        return extension?.GetValue("") as string == ProgId;
    }

    // In tests the root is a scratch key, so the machine-wide registry must not influence the result.
    private bool HasMachineDefault(string extension)
    {
        if (!_isRealRoot) return false;
        using var key = Registry.LocalMachine.OpenSubKey($@"Software\Classes\{extension}");
        return !string.IsNullOrEmpty(key?.GetValue("") as string);
    }

    /// <summary>Opens this app's page in Settings → Default apps, where the user can make it the default.</summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={AppName}") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
    }

    private void NotifyShell()
    {
        // Tell Explorer that associations changed so icons and "Open with" refresh without a sign-out.
        if (_notifyShell) SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private const int ShcneAssocChanged = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
