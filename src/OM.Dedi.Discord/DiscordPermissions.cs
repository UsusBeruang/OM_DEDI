namespace OM.Dedi.Discord;

internal static class DiscordPermissions
{
    public const string DashboardView = "dashboard.view";
    public const string ServerStart = "server.start";
    public const string ServerStop = "server.stop";
    public const string ServerRestart = "server.restart";

    public static string Command(string commandName) =>
        $"command:{commandName}";
}

internal sealed record DiscordAccessContext(
    bool IsOwner,
    IReadOnlyList<string> TierNames,
    IReadOnlySet<string> Permissions)
{
    public bool HasPermission(string permission)
    {
        if (IsOwner)
        {
            return true;
        }

        if (Permissions.Contains("*") ||
            Permissions.Contains(permission))
        {
            return true;
        }

        foreach (var configured in Permissions)
        {
            if (!configured.EndsWith('*'))
            {
                continue;
            }

            var prefix = configured[..^1];
            if (permission.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
