using OM.Dedi.Core;

namespace OM.Dedi.Runtime;

public sealed class ManagedGameServer : IGameServer
{
    private readonly IServerProcess _process;
    private readonly ICommandTransport _transport;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

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
                await _transport.SendAsync(
                    Profile.StopCommand,
                    cancellationToken);

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
        return await SendRawAsync(command, cancellationToken);
    }

    public async Task<CommandResult> SendRawAsync(
        string command,
        CancellationToken cancellationToken = default)
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
