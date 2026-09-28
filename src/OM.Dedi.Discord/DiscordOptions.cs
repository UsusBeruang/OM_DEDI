namespace OM.Dedi.Discord;

public sealed record DiscordOptions
{
    public required ulong GuildId { get; init; }

    public string CommandName { get; init; } = "dedi";

    // Convenience for the common single-owner case.
    public ulong? OwnerUserId { get; init; }

    // Optional additional owners. Owners always bypass every permission check.
    public List<ulong> OwnerUserIds { get; init; } = [];

    // Arbitrary named tiers. Matching multiple tiers combines their permissions.
    public Dictionary<string, DiscordTierOptions> Tiers { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<ulong> GetOwnerUserIds()
    {
        var owners = OwnerUserIds
            .Where(id => id != 0)
            .ToHashSet();

        if (OwnerUserId.HasValue && OwnerUserId.Value != 0)
        {
            owners.Add(OwnerUserId.Value);
        }

        return owners;
    }

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

        if (GetOwnerUserIds().Count == 0)
        {
            throw new InvalidOperationException(
                "Discord control requires at least one ownerUserId or ownerUserIds entry.");
        }

        foreach (var (name, tier) in Tiers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    "Discord tier names must not be empty.");
            }

            if (tier.Permissions.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Discord tier '{name}' must define at least one permission.");
            }
        }
    }
}

public sealed record DiscordTierOptions
{
    public List<ulong> UserIds { get; init; } = [];

    public List<ulong> RoleIds { get; init; } = [];

    public List<string> Permissions { get; init; } = [];
}
