using System.Diagnostics;
using OM.Dedi.Core;

namespace OM.Dedi.Runtime;

public sealed class LocalProcessServer : IServerProcess, IDisposable
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

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _profile.Process.Executable,
                    Arguments = _profile.Process.Arguments,
                    WorkingDirectory = _profile.Process.WorkingDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, args) =>
                PublishOutput(ServerOutputStream.StandardOutput, args.Data);
            process.ErrorDataReceived += (_, args) =>
                PublishOutput(ServerOutputStream.StandardError, args.Data);
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

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
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
        await process.StandardInput.WriteLineAsync(command);
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

    private void PublishOutput(ServerOutputStream stream, string? line)
    {
        if (line is null)
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
