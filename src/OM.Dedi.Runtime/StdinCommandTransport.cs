using OM.Dedi.Core;

namespace OM.Dedi.Runtime;

public sealed class StdinCommandTransport : ICommandTransport
{
    private readonly IInteractiveServerProcess _process;

    public StdinCommandTransport(IInteractiveServerProcess process)
    {
        _process = process;
    }

    public string Name => "stdin";

    public bool IsAvailable => _process.State == ServerState.Running;

    public Task SendAsync(
        string command,
        CancellationToken cancellationToken = default) =>
        _process.WriteLineAsync(command, cancellationToken);
}
