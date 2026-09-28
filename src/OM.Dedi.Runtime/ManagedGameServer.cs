using OM.Dedi.Core;

namespace OM.Dedi.Runtime;

public sealed class ManagedGameServer : IGameServer
{
    private readonly IServerProcess _process;
    private readonly ICommandTransport _transport;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    public ManagedGameServer(
        ServerProfile profile,
        IServerProcess process,
        ICommandTransport transport)
    {
        Profile = profile;
        _process = process;
        _transport = transport;

        _process.OutputReceived += output => OutputReceived?.Invoke(output);
    }

    public ServerProfile Profile { get; }

    public ServerState State => _process.State;

    public int? ProcessId => _process.ProcessId;

    public DateTimeOffset? StartedAt => _process.StartedAt;

    public event Action<ServerOutput>? OutputReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await _process.StartAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_process.State == ServerState.Stopped)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(Profile.StopCommand) &&
                _transport.IsAvailable)
            {
                await _commandLock.WaitAsync(cancellationToken);
                try
                {
                    await _transport.SendAsync(
                        Profile.StopCommand,
                        cancellationToken);
                }
                finally
                {
                    _commandLock.Release();
                }

                var exited = await _process.WaitForExitAsync(
                    TimeSpan.FromSeconds(Profile.ShutdownTimeoutSeconds),
                    cancellationToken);

                if (exited)
                {
                    return;
                }
            }

            await _process.TerminateAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task RestartAsync(
        CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    public async Task<CommandResult> ExecuteAsync(
        string commandName,
        string? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!Profile.Commands.TryGetValue(commandName, out var template))
        {
            return CommandResult.Failure(
                $"Server '{Profile.Id}' does not define command '{commandName}'.");
        }

        var command = ExpandCommand(template, arguments);

        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            if (Profile.CommandCaptures.TryGetValue(
                    commandName,
                    out var captureProfile))
            {
                return await ExecuteCapturedAsync(
                    command,
                    captureProfile,
                    cancellationToken);
            }

            return await SendRawCoreAsync(command, cancellationToken);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<CommandResult> SendRawAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            return await SendRawCoreAsync(command, cancellationToken);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<CommandResult> SendRawCoreAsync(
        string command,
        CancellationToken cancellationToken)
    {
        if (!_transport.IsAvailable)
        {
            return CommandResult.Failure(
                $"Transport '{_transport.Name}' is unavailable for '{Profile.Id}'.");
        }

        try
        {
            await _transport.SendAsync(command, cancellationToken);
            return CommandResult.Success();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CommandResult.Failure(ex.Message);
        }
    }

    private async Task<CommandResult> ExecuteCapturedAsync(
        string command,
        CommandCaptureProfile captureProfile,
        CancellationToken cancellationToken)
    {
        if (!_transport.IsAvailable)
        {
            return CommandResult.Failure(
                $"Transport '{_transport.Name}' is unavailable for '{Profile.Id}'.");
        }

        var output = new List<ServerOutput>();
        var gate = new object();
        DateTimeOffset? lastOutputAt = null;
        var captureActive = false;

        void Capture(ServerOutput item)
        {
            if (!captureActive ||
                (!captureProfile.IncludeStandardError &&
                 item.Stream == ServerOutputStream.StandardError))
            {
                return;
            }

            lock (gate)
            {
                output.Add(item);
                lastOutputAt = DateTimeOffset.UtcNow;
            }
        }

        OutputReceived += Capture;

        try
        {
            captureActive = true;
            await _transport.SendAsync(command, cancellationToken);

            var timeout = TimeSpan.FromMilliseconds(
                Math.Max(100, captureProfile.TimeoutMs));
            var quietPeriod = TimeSpan.FromMilliseconds(
                Math.Max(50, captureProfile.QuietPeriodMs));
            var startedAt = DateTimeOffset.UtcNow;

            while (DateTimeOffset.UtcNow - startedAt < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await Task.Delay(
                    TimeSpan.FromMilliseconds(
                        Math.Min(100, quietPeriod.TotalMilliseconds)),
                    cancellationToken);

                DateTimeOffset? observedLastOutput;
                int outputCount;

                lock (gate)
                {
                    observedLastOutput = lastOutputAt;
                    outputCount = output.Count;
                }

                if (outputCount > 0 &&
                    observedLastOutput.HasValue &&
                    DateTimeOffset.UtcNow - observedLastOutput.Value >= quietPeriod)
                {
                    break;
                }
            }

            IReadOnlyList<ServerOutput> snapshot;
            lock (gate)
            {
                snapshot = output.ToArray();
            }

            return CommandResult.Success(snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CommandResult.Failure(ex.Message);
        }
        finally
        {
            captureActive = false;
            OutputReceived -= Capture;
        }
    }

    private static string ExpandCommand(
        string template,
        string? arguments)
    {
        var value = arguments ?? string.Empty;
        return template.Contains("{args}", StringComparison.Ordinal)
            ? template.Replace("{args}", value, StringComparison.Ordinal).TrimEnd()
            : string.IsNullOrWhiteSpace(value)
                ? template
                : $"{template} {value}";
    }
}
