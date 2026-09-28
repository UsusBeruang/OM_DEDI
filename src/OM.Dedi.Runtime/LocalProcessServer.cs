using System.Diagnostics;
using OM.Dedi.Core;

namespace OM.Dedi.Runtime;

public sealed class LocalProcessServer : IInteractiveServerProcess, IDisposable
{
    private readonly ServerProfile _profile;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private Process? _process;
    private bool _disposed;

    public LocalProcessServer(ServerProfile profile)
    {
        _profile = profile;
    }

    public ServerState State { get; private set; } = ServerState.Stopped;

    public int? ProcessId =>
        _process is { HasExited: false } process ? process.Id : null;

    public DateTimeOffset? StartedAt { get; private set; }

    public event Action<ServerOutput>? OutputReceived;

    public event Action<int>? Exited;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            _process?.Dispose();
            _process = null;

            State = ServerState.Starting;

            var startInfo = new ProcessStartInfo
            {
                FileName = _profile.Process.Executable,
                Arguments = _profile.Process.Arguments,
                WorkingDirectory = _profile.Process.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = _profile.Process.CreateNoWindow
            };

            foreach (var key in _profile.Process.RemoveEnvironmentVariables)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    startInfo.Environment.Remove(key);
                }
            }

            foreach (var (key, value) in _profile.Process.EnvironmentVariables)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    startInfo.Environment[key] = value;
                }
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.Exited += (_, _) => HandleExit(process);

            if (!process.Start())
            {
                process.Dispose();
                State = ServerState.Faulted;
                throw new InvalidOperationException(
                    $"Failed to start server process '{_profile.Id}'.");
            }

            _process = process;
            StartedAt = DateTimeOffset.UtcNow;
            State = ServerState.Running;

            _ = PumpOutputAsync(
                process.StandardOutput,
                ServerOutputStream.StandardOutput);
            _ = PumpOutputAsync(
                process.StandardError,
                ServerOutputStream.StandardError);
        }
        catch
        {
            if (State == ServerState.Starting)
            {
                State = ServerState.Faulted;
            }

            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task WriteLineAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var process = _process;
        if (process is null || process.HasExited || State != ServerState.Running)
        {
            throw new InvalidOperationException(
                $"Server '{_profile.Id}' is not running.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var mode = _profile.Process.InputTerminator.ToLowerInvariant();

        if (mode == "auto")
        {
            if (_profile.Process.TraceInput)
            {
                var escaped = process.StandardInput.NewLine
                    .Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal);

                PublishOutput(
                    ServerOutputStream.StandardError,
                    $"[OM_DEDI STDIN] WriteLine({command}){escaped}");
            }

            await process.StandardInput.WriteLineAsync(command);
            await process.StandardInput.FlushAsync(cancellationToken);
            return;
        }

        var terminator = mode switch
        {
            "cr" => "\r",
            "lf" => "\n",
            "crlf" => "\r\n",
            var value => throw new InvalidOperationException(
                $"Unsupported redirected input terminator '{value}'.")
        };

        if (_profile.Process.TraceInput)
        {
            var escaped = terminator
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);

            PublishOutput(
                ServerOutputStream.StandardError,
                $"[OM_DEDI STDIN] Write({command}{escaped})");
        }

        await process.StandardInput.WriteAsync(command + terminator);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    public async Task TerminateAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var process = _process;
        if (process is null || process.HasExited)
        {
            State = ServerState.Stopped;
            return;
        }

        State = ServerState.Stopping;
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken);
    }

    public async Task<bool> WaitForExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            return true;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task PumpOutputAsync(
        StreamReader reader,
        ServerOutputStream stream)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                PublishOutput(stream, line);
            }
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            // The redirected pipe can close while the process is exiting.
        }
    }

    private void PublishOutput(ServerOutputStream stream, string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        OutputReceived?.Invoke(
            new ServerOutput(DateTimeOffset.UtcNow, stream, line));
    }

    private void HandleExit(Process process)
    {
        var exitCode = process.ExitCode;
        State = ServerState.Stopped;
        StartedAt = null;
        Exited?.Invoke(exitCode);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _process?.Dispose();
        _lifecycleLock.Dispose();
        _disposed = true;
    }
}
