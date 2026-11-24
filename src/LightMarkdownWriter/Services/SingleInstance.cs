using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LightMarkdownWriter.Services;

/// <summary>
/// Keeps one app window per user session. The first launch owns a named mutex and listens on a named pipe;
/// later launches (e.g. double-clicking a .md file) send their file paths through the pipe and exit.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // Per user and per logon session, so other users / RDP sessions get their own instance.
    private static readonly string Name = $"LightMarkdownWriter-{Environment.UserName}-{Process.GetCurrentProcess().SessionId}";
    private static readonly string PipeName = Name + "-open";

    private Mutex? _mutex;
    private CancellationTokenSource? _listening;

    /// <summary>True if this process is the first instance.</summary>
    public bool TryBecomePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + Name, out var createdNew);
        if (createdNew) return true;
        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    /// <summary>Sends files to the running instance. False if it couldn't be reached (then start normally).</summary>
    public static bool SendToPrimary(IEnumerable<string> paths)
    {
        try
        {
            // Resolve here: the running instance has a different working directory than this launch.
            var fullPaths = paths.Select(Path.GetFullPath).ToArray();

            // Let the running instance bring its window to the front (Windows blocks focus stealing otherwise).
            AllowSetForegroundWindow(AsfwAny);

            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeout: 3000);
            using var writer = new StreamWriter(client);
            writer.Write(JsonSerializer.Serialize(fullPaths));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts accepting paths from later launches; <paramref name="onPaths"/> runs on a background thread.</summary>
    public void Listen(Action<string[]> onPaths)
    {
        _listening = new CancellationTokenSource();
        var token = _listening.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server);
                    var json = await reader.ReadToEndAsync(token);
                    onPaths(JsonSerializer.Deserialize<string[]>(json) ?? []);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // A broken message from one launch shouldn't stop listening for the next.
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _listening?.Cancel();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    private const int AsfwAny = -1;

    // Classic DllImport: LibraryImport would need AllowUnsafeBlocks for the whole project for this one call.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
