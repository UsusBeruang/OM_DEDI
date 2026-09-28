namespace OM.Dedi.Discord;

public sealed record DiscordOptions
{
    public required ulong GuildId { get; init; }

    public string CommandName { get; init; } = "dedi";

    public List<ulong> AllowedUserIds { get; init; } = [];

    public List<ulong> AllowedRoleIds { get; init; } = [];

    public void Validate()
    {
        if (GuildId == 0)
        {
            throw new InvalidOperationException(
                "Discord GuildId must be configured.");
        }

        if (string.IsNullOrWhiteSpace(CommandName))
        {
            throw new InvalidOperationException(
                "Discord CommandName must not be empty.");
        }

        if (AllowedUserIds.Count == 0 && AllowedRoleIds.Count == 0)
        {
            throw new InvalidOperationException(
                "Discord control is default-deny. Configure at least one allowed user or role.");
        }
    }
}
