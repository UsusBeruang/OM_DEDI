using OM.Dedi.Core;

namespace OM.Dedi.Runtime;

public static class GameServerFactory
{
    public static IGameServer Create(ServerProfile profile)
    {
        var process = new LocalProcessServer(profile);

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
