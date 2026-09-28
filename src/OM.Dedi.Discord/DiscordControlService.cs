using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using OM.Dedi.Core;

namespace OM.Dedi.Discord;

public sealed class DiscordControlService : IAsyncDisposable
{
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(2);

    private readonly string _token;
    private readonly DiscordOptions _options;
    private readonly IReadOnlyDictionary<string, IGameServer> _servers;
    private readonly DiscordSocketClient _client;
    private readonly DiscordAuthorization _authorization;
    private readonly ConcurrentDictionary<string, PendingConfirmation> _pending = new();

    private int _commandsRegistered;
    private bool _disposed;

    public DiscordControlService(
        string token,
        DiscordOptions options,
        IReadOnlyDictionary<string, IGameServer> servers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(servers);

        options.Validate();

        _token = token;
        _options = options;
        _servers = servers;
        _authorization = new DiscordAuthorization(options);

        _client = new DiscordSocketClient(
            new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds,
                LogGatewayIntentWarnings = false
            });

        _client.Log += OnLogAsync;
        _client.Ready += OnReadyAsync;
        _client.SlashCommandExecuted += OnSlashCommandAsync;
        _client.ButtonExecuted += OnButtonAsync;
    }

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _client.LoginAsync(TokenType.Bot, _token);
        await _client.StartAsync();
    }

    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _client.StopAsync();
        await _client.LogoutAsync();
    }

    private Task OnLogAsync(LogMessage message)
    {
        Console.WriteLine(
            $"[{DateTimeOffset.Now:HH:mm:ss}] [Discord] [{message.Severity}] {message.Message}");

        if (message.Exception is not null)
        {
            Console.WriteLine(message.Exception);
        }

        return Task.CompletedTask;
    }

    private async Task OnReadyAsync()
    {
        if (Interlocked.Exchange(ref _commandsRegistered, 1) != 0)
        {
            return;
        }

        var guild = _client.GetGuild(_options.GuildId);
        if (guild is null)
        {
            Interlocked.Exchange(ref _commandsRegistered, 0);
            Console.Error.WriteLine(
                $"Discord guild '{_options.GuildId}' is not available to the bot.");
            return;
        }

        var command = BuildCommand();
        await guild.BulkOverwriteApplicationCommandAsync([command.Build()]);

        Console.WriteLine(
            $"Discord command '/{_options.CommandName}' registered for guild '{guild.Name}'.");
    }

    private SlashCommandBuilder BuildCommand()
    {
        return new SlashCommandBuilder()
            .WithName(_options.CommandName)
            .WithDescription("Control dedicated servers managed by OM_DEDI")
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName("servers")
                    .WithDescription("List configured servers")
                    .WithType(ApplicationCommandOptionType.SubCommand))
            .AddOption(CreateServerSubcommand("status", "Show server status"))
            .AddOption(CreateServerSubcommand("start", "Start a server"))
            .AddOption(CreateServerSubcommand("stop", "Stop a server"))
            .AddOption(CreateServerSubcommand("restart", "Restart a server"))
            .AddOption(
                CreateServerSubcommand(
                        "exec",
                        "Execute a command declared by the server profile")
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName("command")
                            .WithDescription("Profile command name")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(true))
                    .AddOption(
                        new SlashCommandOptionBuilder()
                            .WithName("arguments")
                            .WithDescription("Optional command arguments")
                            .WithType(ApplicationCommandOptionType.String)
                            .WithRequired(false)));
    }

    private static SlashCommandOptionBuilder CreateServerSubcommand(
        string name,
        string description) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName("server")
                    .WithDescription("OM_DEDI server id")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true));

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (!_authorization.IsAllowed(command))
        {
            await command.RespondAsync(
                "You are not authorized to control OM_DEDI.",
                ephemeral: true);
            return;
        }

        var subcommand = command.Data.Options.FirstOrDefault();
        if (subcommand is null)
        {
            await command.RespondAsync(
                "No subcommand was supplied.",
                ephemeral: true);
            return;
        }

        try
        {
            switch (subcommand.Name)
            {
                case "servers":
                    await RespondWithServersAsync(command);
                    break;

                case "status":
                    await RespondWithStatusAsync(command, subcommand);
                    break;

                case "start":
                    await StartServerAsync(command, subcommand);
                    break;

                case "stop":
                case "restart":
                    await RequestConfirmationAsync(
                        command,
                        subcommand,
                        subcommand.Name);
                    break;

                case "exec":
                    await ExecuteProfileCommandAsync(command, subcommand);
                    break;

                default:
                    await command.RespondAsync(
                        "Unsupported OM_DEDI command.",
                        ephemeral: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            await RespondOrFollowupAsync(
                command,
                $"Operation failed: {ex.Message}");
        }
    }

    private async Task RespondWithServersAsync(SocketSlashCommand command)
    {
        var lines = _servers.Values
            .OrderBy(server => server.Profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(FormatStatus);

        await command.RespondAsync(
            "Configured servers:\n" + string.Join(Environment.NewLine, lines),
            ephemeral: true);
    }

    private async Task RespondWithStatusAsync(
        SocketSlashCommand command,
        SocketSlashCommandDataOption subcommand)
    {
        if (!TryResolveServer(subcommand, out var server, out var error))
        {
            await command.RespondAsync(error, ephemeral: true);
            return;
        }

        await command.RespondAsync(
            FormatStatus(server),
            ephemeral: true);
    }

    private async Task StartServerAsync(
        SocketSlashCommand command,
        SocketSlashCommandDataOption subcommand)
    {
        if (!TryResolveServer(subcommand, out var server, out var error))
        {
            await command.RespondAsync(error, ephemeral: true);
            return;
        }

        await command.DeferAsync(ephemeral: true);
        await server.StartAsync();

        await command.FollowupAsync(
            $"Started {server.Profile.Id}. State: {server.State}.",
            ephemeral: true);
    }

    private async Task RequestConfirmationAsync(
        SocketSlashCommand command,
        SocketSlashCommandDataOption subcommand,
        string action)
    {
        if (!TryResolveServer(subcommand, out var server, out var error))
        {
            await command.RespondAsync(error, ephemeral: true);
            return;
        }

        var nonce = Guid.NewGuid().ToString("N");
        _pending[nonce] = new PendingConfirmation(
            command.User.Id,
            action,
            server.Profile.Id,
            DateTimeOffset.UtcNow.Add(ConfirmationLifetime));

        var components = new ComponentBuilder()
            .WithButton(
                "Confirm",
                $"dedi:confirm:{nonce}",
                ButtonStyle.Danger)
            .WithButton(
                "Cancel",
                $"dedi:cancel:{nonce}",
                ButtonStyle.Secondary)
            .Build();

        await command.RespondAsync(
            $"Confirm {action} for {server.Profile.Id}?",
            components: components,
            ephemeral: true);
    }

    private async Task ExecuteProfileCommandAsync(
        SocketSlashCommand command,
        SocketSlashCommandDataOption subcommand)
    {
        if (!TryResolveServer(subcommand, out var server, out var error))
        {
            await command.RespondAsync(error, ephemeral: true);
            return;
        }

        var commandName = GetStringOption(subcommand, "command");
        if (string.IsNullOrWhiteSpace(commandName))
        {
            await command.RespondAsync(
                "A profile command is required.",
                ephemeral: true);
            return;
        }

        if (!server.Profile.Commands.ContainsKey(commandName))
        {
            var supported = server.Profile.Commands.Keys
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

            await command.RespondAsync(
                $"{commandName} is not declared by {server.Profile.Id}. " +
                $"Allowed commands: {string.Join(", ", supported)}",
                ephemeral: true);
            return;
        }

        var arguments = GetStringOption(subcommand, "arguments");
        var result = await server.ExecuteAsync(commandName, arguments);

        await command.RespondAsync(
            result.Succeeded
                ? $"Sent {commandName} to {server.Profile.Id}."
                : $"Command failed: {result.Error}",
            ephemeral: true);
    }

    private async Task OnButtonAsync(SocketMessageComponent component)
    {
        if (!_authorization.IsAllowed(component))
        {
            await component.RespondAsync(
                "You are not authorized to control OM_DEDI.",
                ephemeral: true);
            return;
        }

        var parts = component.Data.CustomId.Split(':', 3);
        if (parts.Length != 3 || parts[0] != "dedi")
        {
            return;
        }

        var mode = parts[1];
        var nonce = parts[2];

        if (!_pending.TryRemove(nonce, out var pending))
        {
            await component.RespondAsync(
                "This confirmation has expired or was already used.",
                ephemeral: true);
            return;
        }

        if (pending.UserId != component.User.Id)
        {
            _pending[nonce] = pending;
            await component.RespondAsync(
                "Only the user who requested this operation can confirm it.",
                ephemeral: true);
            return;
        }

        if (pending.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await component.RespondAsync(
                "This confirmation has expired.",
                ephemeral: true);
            return;
        }

        if (mode == "cancel")
        {
            await component.UpdateAsync(
                properties =>
                {
                    properties.Content =
                        $"Cancelled {pending.Action} for {pending.ServerId}.";
                    properties.Components = new ComponentBuilder().Build();
                });
            return;
        }

        if (mode != "confirm" ||
            !_servers.TryGetValue(pending.ServerId, out var server))
        {
            await component.RespondAsync(
                "The requested operation is no longer available.",
                ephemeral: true);
            return;
        }

        await component.DeferAsync(ephemeral: true);

        try
        {
            switch (pending.Action)
            {
                case "stop":
                    await server.StopAsync();
                    break;

                case "restart":
                    await server.RestartAsync();
                    break;

                default:
                    await component.FollowupAsync(
                        "Unsupported confirmed operation.",
                        ephemeral: true);
                    return;
            }

            await component.FollowupAsync(
                $"Completed {pending.Action} for {pending.ServerId}. " +
                $"State: {server.State}.",
                ephemeral: true);
        }
        catch (Exception ex)
        {
            await component.FollowupAsync(
                $"Operation failed: {ex.Message}",
                ephemeral: true);
        }
    }

    private bool TryResolveServer(
        SocketSlashCommandDataOption subcommand,
        out IGameServer server,
        out string error)
    {
        var serverId = GetStringOption(subcommand, "server");
        if (!string.IsNullOrWhiteSpace(serverId) &&
            _servers.TryGetValue(serverId, out var resolvedServer))
        {
            server = resolvedServer;
            error = string.Empty;
            return true;
        }

        server = null!;
        error = string.IsNullOrWhiteSpace(serverId)
            ? "A server id is required."
            : $"Unknown server id {serverId}.";
        return false;
    }

    private static string? GetStringOption(
        SocketSlashCommandDataOption subcommand,
        string name)
    {
        var option = subcommand.Options.FirstOrDefault(
            item => string.Equals(
                item.Name,
                name,
                StringComparison.OrdinalIgnoreCase));

        return option?.Value?.ToString();
    }

    private static string FormatStatus(IGameServer server)
    {
        var pid = server.ProcessId?.ToString() ?? "-";
        return $"{server.Profile.Id,-18} {server.State,-10} PID={pid}  {server.Profile.Name}";
    }

    private static async Task RespondOrFollowupAsync(
        SocketSlashCommand command,
        string message)
    {
        if (command.HasResponded)
        {
            await command.FollowupAsync(message, ephemeral: true);
        }
        else
        {
            await command.RespondAsync(message, ephemeral: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await _client.StopAsync();
            await _client.LogoutAsync();
        }
        finally
        {
            _client.Dispose();
        }
    }

    private sealed record PendingConfirmation(
        ulong UserId,
        string Action,
        string ServerId,
        DateTimeOffset ExpiresAt);
}
