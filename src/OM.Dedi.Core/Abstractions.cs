namespace OM.Dedi.Core;

public interface IServerProcess
{
    ServerState State { get; }

    int? ProcessId { get; }

    DateTimeOffset? StartedAt { get; }

    event Action<ServerOutput>? OutputReceived;

    event Action<int>? Exited;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task TerminateAsync(CancellationToken cancellationToken = default);

    Task<bool> WaitForExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public interface ICommandTransport
{
    string Name { get; }

    bool IsAvailable { get; }

    Task SendAsync(
        string command,
        CancellationToken cancellationToken = default);
}

public interface IGameServer
{
    ServerProfile Profile { get; }

    ServerState State { get; }

    int? ProcessId { get; }

    DateTimeOffset? StartedAt { get; }

    event Action<ServerOutput>? OutputReceived;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task RestartAsync(CancellationToken cancellationToken = default);

    Task<CommandResult> ExecuteAsync(
        string commandName,
        string? arguments = null,
        CancellationToken cancellationToken = default);

    Task<CommandResult> SendRawAsync(
        string command,
        CancellationToken cancellationToken = default);
}
