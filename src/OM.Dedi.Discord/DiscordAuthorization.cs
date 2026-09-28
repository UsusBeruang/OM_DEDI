using Discord.WebSocket;

namespace OM.Dedi.Discord;

internal sealed class DiscordAuthorization
{
    private readonly DiscordOptions _options;
    private readonly IReadOnlySet<ulong> _owners;

    public DiscordAuthorization(DiscordOptions options)
    {
        _options = options;
        _owners = options.GetOwnerUserIds();
    }

    public bool IsAllowed(SocketSlashCommand command) =>
        HasPermission(command, DiscordPermissions.DashboardView);

    public bool IsAllowed(SocketMessageComponent component) =>
        HasPermission(component, DiscordPermissions.DashboardView);

    public bool IsAllowed(SocketModal modal) =>
        HasPermission(modal, DiscordPermissions.DashboardView);

    public bool HasPermission(
        SocketSlashCommand command,
        string permission) =>
        command.GuildId == _options.GuildId &&
        command.User is SocketGuildUser user &&
        Resolve(user).HasPermission(permission);

    public bool HasPermission(
        SocketMessageComponent component,
        string permission) =>
        component.GuildId == _options.GuildId &&
        component.User is SocketGuildUser user &&
        Resolve(user).HasPermission(permission);

    public bool HasPermission(
        SocketModal modal,
        string permission) =>
        modal.GuildId == _options.GuildId &&
        modal.User is SocketGuildUser user &&
        Resolve(user).HasPermission(permission);

    public bool HasPermission(
        SocketGuildUser user,
        string permission) =>
        user.Guild.Id == _options.GuildId &&
        Resolve(user).HasPermission(permission);

    public DiscordAccessContext Resolve(SocketGuildUser user)
    {
        if (user.Guild.Id != _options.GuildId)
        {
            return new DiscordAccessContext(
                false,
                Array.Empty<string>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        if (_owners.Contains(user.Id))
        {
            return new DiscordAccessContext(
                true,
                new[] { "Owner" },
                new HashSet<string>(
                    new[] { "*" },
                    StringComparer.OrdinalIgnoreCase));
        }

        var tierNames = new List<string>();
        var permissions = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (name, tier) in _options.Tiers)
        {
            var userMatch = tier.UserIds.Contains(user.Id);
            var roleMatch = user.Roles.Any(
                role => tier.RoleIds.Contains(role.Id));

            if (!userMatch && !roleMatch)
            {
                continue;
            }

            tierNames.Add(name);

            foreach (var permission in tier.Permissions)
            {
                if (!string.IsNullOrWhiteSpace(permission))
                {
                    permissions.Add(permission.Trim());
                }
            }
        }

        return new DiscordAccessContext(
            false,
            tierNames,
            permissions);
    }
}
