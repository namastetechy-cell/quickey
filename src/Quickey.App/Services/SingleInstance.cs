using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace Quickey.App.Services;

/// <summary>
/// Keeps one Quickey per user. Later launches (e.g. from the Explorer menu, once per selected file)
/// hand their arguments to the running instance over a named pipe and exit, unless the later
/// launch is a newer version: then the old instance is asked to quit (or, if it predates that
/// handshake, stopped) and the new one takes over. Otherwise installing an update while Quickey
/// was running left the old version in charge.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Name = "Quickey-" + Environment.UserName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, string? previousVersion)
    {
        _mutex = mutex;
        PreviousVersion = previousVersion;
    }

    public event Action<string[]>? ArgumentsReceived;

    /// <summary>The version that ran before this one, if different: Quickey was just updated.</summary>
    public string? PreviousVersion { get; }

    public const string QuitForUpdateArgument = "--quit-for-update";

    private static string RunningVersionFile => Path.Combine(AppPaths.Root, "running-version.txt");

    /// <summary>Returns the instance if this process should run; otherwise forwards args and returns null.</summary>
    public static SingleInstance? TryAcquire(string[] args)
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + Name, out var createdNew);
        if (createdNew)
            return Started(mutex);

        var running = ReadRunningVersion();
        if (IsNewer(Log.Version, running))
        {
            Log.Info($"Quickey {Log.Version} replacing running {running ?? "older version"}");
            Forward([QuitForUpdateArgument]);
            if (WaitFor(mutex, TimeSpan.FromSeconds(8)) || (StopOtherInstances() && WaitFor(mutex, TimeSpan.FromSeconds(5))))
                return Started(mutex);
            Log.Error("Couldn't replace the running Quickey");
        }

        mutex.Dispose();
        Forward(args);
        return null;
    }

    /// <summary>
    /// True if <paramref name="mine"/> is newer than the running instance's version. A running
    /// instance that never recorded its version is from before 0.1.10, so older.
    /// </summary>
    public static bool IsNewer(string mine, string? running) =>
        Version.TryParse(mine, out var me) && (running is null || (Version.TryParse(running, out var them) && me > them));

    private static SingleInstance Started(Mutex mutex)
    {
        var previous = ReadRunningVersion();
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(RunningVersionFile, Log.Version);
        }
        catch (IOException)
        {
        }
        return new SingleInstance(mutex, previous is not null && previous != Log.Version ? previous : null);
    }

    private static string? ReadRunningVersion()
    {
        try
        {
            return File.Exists(RunningVersionFile) ? File.ReadAllText(RunningVersionFile).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool WaitFor(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true; // the old instance ended without releasing it: ours now
        }
    }

    /// <summary>For old versions that don't understand the quit request: end their process.</summary>
    private static bool StopOtherInstances()
    {
        using var self = Process.GetCurrentProcess();
        var stopped = false;
        foreach (var other in Process.GetProcessesByName(self.ProcessName))
        {
            using (other)
            {
                if (other.Id == self.Id || other.SessionId != self.SessionId)
                    continue;
                try
                {
                    other.Kill();
                    other.WaitForExit(3000);
                    stopped = true;
                    Log.Info($"Stopped old Quickey process {other.Id}");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        return stopped;
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
