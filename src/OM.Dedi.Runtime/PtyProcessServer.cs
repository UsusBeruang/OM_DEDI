using System.Text;
using OM.Dedi.Core;
using Porta.Pty;

namespace OM.Dedi.Runtime;

public sealed class PtyProcessServer : IInteractiveServerProcess, IDisposable
{
    private readonly ServerProfile _profile;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private IPtyConnection? _connection;
    private bool _disposed;

    public PtyProcessServer(ServerProfile profile)
    {
        _profile = profile;
    }

    public ServerState State { get; private set; } = ServerState.Stopped;

    public int? ProcessId =>
        _connection is not null &&
        State is ServerState.Starting or ServerState.Running or ServerState.Stopping
            ? _connection.Pid
            : null;

    public DateTimeOffset? StartedAt { get; private set; }

    public event Action<ServerOutput>? OutputReceived;

    public event Action<int>? Exited;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null &&
                State is ServerState.Starting or ServerState.Running)
            {
                return;
            }

            _connection?.Dispose();
            _connection = null;

            State = ServerState.Starting;

            var process = _profile.Process;
            var options = new PtyOptions
            {
                Name = _profile.Id,
                App = process.Executable,
                Cwd = process.WorkingDirectory,
                Cols = Math.Max(20, process.TerminalColumns),
                Rows = Math.Max(5, process.TerminalRows),
                CommandLine = string.IsNullOrWhiteSpace(process.Arguments)
                    ? Array.Empty<string>()
                    : new[] { process.Arguments },
                VerbatimCommandLine = !string.IsNullOrWhiteSpace(process.Arguments)
            };

            var connection = await PtyProvider.SpawnAsync(
                options,
                cancellationToken);

            connection.ProcessExited += (_, args) =>
                HandleExit(args.ExitCode);

            _connection = connection;
            StartedAt = DateTimeOffset.UtcNow;
            State = ServerState.Running;

            _ = PumpOutputAsync(connection.ReaderStream);
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

        var connection = _connection;
        if (connection is null || State != ServerState.Running)
        {
            throw new InvalidOperationException(
                $"Server '{_profile.Id}' is not running.");
        }

        var terminator = _profile.Process.InputTerminator.ToLowerInvariant() switch
        {
            "auto" => "\r",
            "cr" => "\r",
            "lf" => "\n",
            "crlf" => "\r\n",
            var value => throw new InvalidOperationException(
                $"Unsupported PTY input terminator '{value}'.")
        };

        var bytes = Encoding.UTF8.GetBytes(command + terminator);

        if (_profile.Process.TraceInput)
        {
            var escaped = terminator
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);

            OutputReceived?.Invoke(
                new ServerOutput(
                    DateTimeOffset.UtcNow,
                    ServerOutputStream.StandardError,
                    $"[OM_DEDI PTY IN] {command}{escaped} ({bytes.Length} bytes)"));
        }

        await connection.WriterStream.WriteAsync(
            bytes,
            0,
            bytes.Length,
            cancellationToken);

        await connection.WriterStream.FlushAsync(cancellationToken);
    }

    public async Task TerminateAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var connection = _connection;
        if (connection is null ||
            State is ServerState.Stopped or ServerState.Faulted)
        {
            State = ServerState.Stopped;
            return;
        }

        State = ServerState.Stopping;
        connection.Kill();

        await WaitForExitAsync(
            TimeSpan.FromSeconds(5),
            cancellationToken);
    }

    public async Task<bool> WaitForExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var connection = _connection;
        if (connection is null || State == ServerState.Stopped)
        {
            return true;
        }

        var milliseconds = (int)Math.Clamp(
            timeout.TotalMilliseconds,
            1,
            int.MaxValue);

        return await Task.Run(
            () => connection.WaitForExit(milliseconds),
            cancellationToken);
    }

    private async Task PumpOutputAsync(Stream reader)
    {
        var buffer = new byte[4096];

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(
                    buffer,
                    0,
                    buffer.Length);

                if (read == 0)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(
                    buffer,
                    0,
                    read);

                if (!string.IsNullOrEmpty(text))
                {
                    OutputReceived?.Invoke(
                        new ServerOutput(
                            DateTimeOffset.UtcNow,
                            ServerOutputStream.StandardOutput,
                            text));
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            // PTY streams close as part of normal process teardown.
        }
    }

    private void HandleExit(int exitCode)
    {
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

        _connection?.Dispose();
        _connection = null;

        _lifecycleLock.Dispose();
        _disposed = true;
    }
}
