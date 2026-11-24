using LightMarkdownWriter.Services;
using Microsoft.Win32;

namespace LightMarkdownWriter.Tests;

/// <summary>Runs against a throwaway HKCU key (deleted afterwards), never the real file associations.</summary>
public sealed class FileAssociationServiceTests : IDisposable
{
    private const string TestParent = @"Software\LightMarkdownWriter.Tests";
    private readonly string _rootPath = $@"{TestParent}\{Guid.NewGuid():N}";
    private readonly RegistryKey _root;
    private const string ExePath = @"C:\Apps\LightMarkdownWriter\LightMarkdownWriter.exe";

    public FileAssociationServiceTests() => _root = Registry.CurrentUser.CreateSubKey(_rootPath);

    private FileAssociationService Service(string exePath = ExePath) => new(_root, exePath, notifyShell: false);

    [Fact]
    public void Register_writes_progid_open_with_and_capabilities()
    {
        Service().Register();

        Assert.Equal($"\"{ExePath}\" \"%1\"",
            _root.OpenSubKey(@"Classes\LightMarkdownWriter.md\shell\open\command")?.GetValue(""));
        Assert.Equal($"\"{ExePath}\",0", _root.OpenSubKey(@"Classes\LightMarkdownWriter.md\DefaultIcon")?.GetValue(""));
        foreach (var extension in new[] { ".md", ".markdown" })
        {
            Assert.Contains("LightMarkdownWriter.md", _root.OpenSubKey($@"Classes\{extension}\OpenWithProgids")!.GetValueNames());
            Assert.Equal("LightMarkdownWriter.md",
                _root.OpenSubKey(@"LightMarkdownWriter\Capabilities\FileAssociations")?.GetValue(extension));
        }
        Assert.Equal(@"Software\LightMarkdownWriter\Capabilities", _root.OpenSubKey("RegisteredApplications")?.GetValue("LightMarkdownWriter"));
        Assert.Equal(AssociationState.Registered, Service().GetState());
    }

    [Fact]
    public void Moved_exe_is_reported_and_unregister_removes_everything()
    {
        Service().Register();
        Assert.Equal(AssociationState.RegisteredElsewhere, Service(@"D:\Elsewhere\LightMarkdownWriter.exe").GetState());

        Service().Unregister();

        Assert.Equal(AssociationState.NotRegistered, Service().GetState());
        Assert.Null(_root.OpenSubKey(@"Classes\LightMarkdownWriter.md"));
        Assert.Null(_root.OpenSubKey(@"Classes\Applications\LightMarkdownWriter.exe"));
        Assert.DoesNotContain("LightMarkdownWriter.md", _root.OpenSubKey(@"Classes\.md\OpenWithProgids")!.GetValueNames());
        Assert.Null(_root.OpenSubKey(@"LightMarkdownWriter\Capabilities"));
        Assert.DoesNotContain("LightMarkdownWriter", _root.OpenSubKey("RegisteredApplications")!.GetValueNames());
    }

    [Fact]
    public void Unregister_when_never_registered_is_harmless()
    {
        Service().Unregister();
        Assert.Equal(AssociationState.NotRegistered, Service().GetState());
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(TestParent);
        if (parent is { SubKeyCount: 0, ValueCount: 0 })
            Registry.CurrentUser.DeleteSubKey(TestParent, throwOnMissingSubKey: false);
    }
}
