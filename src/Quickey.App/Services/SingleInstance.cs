using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace Quickey.App.Services;

/// <summary>
/// Keeps one Quickey per user. Later launches (e.g. from the Explorer menu, once per selected file)
/// hand their arguments to the running instance over a named pipe and exit.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Name = "Quickey-" + Environment.UserName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public event Action<string[]>? ArgumentsReceived;

    /// <summary>Returns the instance if this process is the first; otherwise forwards args and returns null.</summary>
    public static SingleInstance? TryAcquire(string[] args)
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + Name, out var createdNew);
        if (createdNew)
            return new SingleInstance(mutex);

        mutex.Dispose();
        Forward(args);
        return null;
    }

    public void StartListening() => _ = Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server);
                var args = JsonSerializer.Deserialize<string[]>(await reader.ReadToEndAsync(_stop.Token));
                if (args is not null)
                    ArgumentsReceived?.Invoke(args);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // A client that disconnects mid-message shouldn't take the listener down.
            }
        }
    }

    private static void Forward(string[] args)
    {
        // Explorer gave this process the right to take focus; pass it on so the wheel can open in front.
        Native.Win32.AllowSetForegroundWindow(Native.Win32.ASFW_ANY);
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client);
            writer.Write(JsonSerializer.Serialize(args));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // The first instance is shutting down; nothing useful to do.
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
