using OM.Dedi.Core;

namespace OM.Dedi.Runtime;

public static class GameServerFactory
{
    public static IGameServer Create(ServerProfile profile)
    {
        IInteractiveServerProcess process =
            profile.Process.Mode.ToLowerInvariant() switch
            {
                "redirected" => new LocalProcessServer(profile),
                "pty" or "conpty" => new PtyProcessServer(profile),
                var mode => throw new NotSupportedException(
                    $"Process mode '{mode}' is not supported.")
            };

        ICommandTransport transport =
            profile.Transport.Type.ToLowerInvariant() switch
            {
                "stdin" => new StdinCommandTransport(process),
                var type => throw new NotSupportedException(
                    $"Transport '{type}' is not supported yet.")
            };

        return new ManagedGameServer(profile, process, transport);
    }
}
