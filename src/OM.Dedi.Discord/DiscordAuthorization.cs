using Discord.WebSocket;

namespace OM.Dedi.Discord;

internal sealed class DiscordAuthorization
{
    private readonly DiscordOptions _options;
    private readonly HashSet<ulong> _allowedUsers;
    private readonly HashSet<ulong> _allowedRoles;

    public DiscordAuthorization(DiscordOptions options)
    {
        _options = options;
        _allowedUsers = options.AllowedUserIds.ToHashSet();
        _allowedRoles = options.AllowedRoleIds.ToHashSet();
    }

    public bool IsAllowed(SocketSlashCommand command) =>
        command.GuildId == _options.GuildId &&
        command.User is SocketGuildUser user &&
        IsAllowed(user);

    public bool IsAllowed(SocketMessageComponent component) =>
        component.GuildId == _options.GuildId &&
        component.User is SocketGuildUser user &&
        IsAllowed(user);

    private bool IsAllowed(SocketGuildUser user)
    {
        if (_allowedUsers.Contains(user.Id))
        {
            return true;
        }

        return user.Roles.Any(role => _allowedRoles.Contains(role.Id));
    }
}
