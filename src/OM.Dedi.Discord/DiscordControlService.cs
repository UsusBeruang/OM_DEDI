using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using OM.Dedi.Core;

namespace OM.Dedi.Discord;

public sealed class DiscordControlService : IAsyncDisposable
{
    private static readonly Regex NetworkEndpointRegex = new(
        @"(?<![0-9A-Fa-f:.])(?:(?:\d{1,3}\.){3}\d{1,3}|\[[0-9A-Fa-f:]+\]|[0-9A-Fa-f:]{2,})(?::\d{1,5})?(?![0-9A-Fa-f:.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PanelLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CommandPromptLifetime = TimeSpan.FromMinutes(2);

    private const string DashboardStartId = "dedi:dashboard:start";
    private const string DashboardManageId = "dedi:dashboard:manage";
    private const string DashboardRefreshId = "dedi:dashboard:refresh";
    private const string StartSelectId = "dedi:select:start";
    private const string ManageSelectId = "dedi:select:manage";

    private readonly string _token;
    private readonly DiscordOptions _options;
    private readonly IReadOnlyDictionary<string, IGameServer> _servers;
    private readonly DiscordSocketClient _client;
    private readonly DiscordAuthorization _authorization;
    private readonly ConcurrentDictionary<string, PendingConfirmation> _pending = new();
    private readonly ConcurrentDictionary<string, ServerPanelSession> _panels = new();
    private readonly ConcurrentDictionary<string, CommandPrompt> _commandPrompts = new();

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
        _client.SelectMenuExecuted += OnSelectMenuAsync;
        _client.ModalSubmitted += OnModalSubmittedAsync;
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

    private SlashCommandBuilder BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(_options.CommandName)
            .WithDescription("Open the OM_DEDI dedicated server control panel");

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (!_authorization.IsAllowed(command))
        {
            await command.RespondAsync(
                "You are not authorized to control OM_DEDI.",
                ephemeral: true);
            return;
        }

        CleanupExpiredSessions();

        await command.RespondAsync(
            BuildDashboardText(),
            components: BuildDashboardComponents(
                (SocketGuildUser)command.User),
            ephemeral: true);
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

        _ = Task.Run(
            () => RunStartServerAsync(command, server));
    }

    private static async Task RunStartServerAsync(
        SocketSlashCommand command,
        IGameServer server)
    {
        try
        {
            await server.StartAsync();

            await command.ModifyOriginalResponseAsync(
                properties =>
                {
                    properties.Content =
                        $"Started {server.Profile.Id}. State: {server.State}.";
                });
        }
        catch (Exception ex)
        {
            await command.ModifyOriginalResponseAsync(
                properties =>
                {
                    properties.Content =
                        $"Start failed for {server.Profile.Id}: {ex.Message}";
                });
        }
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

        CleanupExpiredSessions();

        switch (component.Data.CustomId)
        {
            case DashboardStartId:
                await ShowStartableServersAsync(component);
                return;

            case DashboardManageId:
                await ShowRunningServersAsync(component);
                return;

            case DashboardRefreshId:
                await ShowDashboardAsync(component);
                return;
        }

        var parts = component.Data.CustomId.Split(':');

        if (parts.Length == 4 &&
            parts[0] == "dedi" &&
            parts[1] == "panel")
        {
            await HandleServerPanelButtonAsync(
                component,
                parts[2],
                parts[3]);
            return;
        }

        if (parts.Length == 3 &&
            parts[0] == "dedi" &&
            (parts[1] == "confirm" || parts[1] == "cancel"))
        {
            await HandleConfirmationAsync(
                component,
                parts[1],
                parts[2]);
        }
    }

    private async Task RunConfirmedOperationAsync(
        SocketMessageComponent component,
        PendingConfirmation pending,
        IGameServer server)
    {
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
                    throw new InvalidOperationException(
                        "Unsupported confirmed action '" + pending.Action + "'.");
            }

            await component.ModifyOriginalResponseAsync(
                properties =>
                {
                    properties.Content =
                        "Completed " + pending.Action + " for " +
                        server.Profile.Name + ". State: " + server.State +
                        ".\n\n" + BuildDashboardText();
                    properties.Components = BuildDashboardComponents((SocketGuildUser)component.User);
                });
        }
        catch (Exception ex)
        {
            await component.ModifyOriginalResponseAsync(
                properties =>
                {
                    properties.Content =
                        "Operation failed: " + ex.Message +
                        "\n\n" + BuildDashboardText();
                    properties.Components = BuildDashboardComponents((SocketGuildUser)component.User);
                });
        }
    }

    private async Task OnSelectMenuAsync(SocketMessageComponent component)
    {
        if (!_authorization.IsAllowed(component))
        {
            await component.RespondAsync(
                "You are not authorized to control OM_DEDI.",
                ephemeral: true);
            return;
        }

        CleanupExpiredSessions();

        var selected = component.Data.Values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(selected))
        {
            await component.RespondAsync(
                "No selection was provided.",
                ephemeral: true);
            return;
        }

        switch (component.Data.CustomId)
        {
            case StartSelectId:
                await StartSelectedServerAsync(component, selected);
                return;

            case ManageSelectId:
                await ShowServerPanelAsync(component, selected);
                return;
        }

        var parts = component.Data.CustomId.Split(':');
        if (parts.Length == 3 &&
            parts[0] == "dedi" &&
            parts[1] == "commands")
        {
            await ExecuteSelectedProfileCommandAsync(
                component,
                parts[2],
                selected);
        }
    }

    private async Task OnModalSubmittedAsync(SocketModal modal)
    {
        if (!_authorization.IsAllowed(modal))
        {
            await modal.RespondAsync(
                "You are not authorized to control OM_DEDI.",
                ephemeral: true);
            return;
        }

        CleanupExpiredSessions();

        var parts = modal.Data.CustomId.Split(':');
        if (parts.Length != 3 ||
            parts[0] != "dedi" ||
            parts[1] != "modal" ||
            !_commandPrompts.TryRemove(parts[2], out var prompt) ||
            prompt.UserId != modal.User.Id ||
            prompt.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await modal.RespondAsync(
                "This command prompt has expired.",
                ephemeral: true);
            return;
        }

        if (!_authorization.HasPermission(
                modal,
                DiscordPermissions.Command(prompt.CommandName)))
        {
            await modal.RespondAsync(
                "Your tier no longer allows this server command.",
                ephemeral: true);
            return;
        }

        if (!_servers.TryGetValue(prompt.ServerId, out var server) ||
            server.State != ServerState.Running)
        {
            await modal.RespondAsync(
                "The selected server is no longer running.",
                ephemeral: true);
            return;
        }

        var arguments = modal.Data.Components
            .FirstOrDefault(item => item.CustomId == "arguments")
            ?.Value;

        await modal.DeferAsync(ephemeral: true);

        _ = Task.Run(
            () => RunProfileCommandAsync(
                modal,
                server,
                prompt.CommandName,
                arguments));
    }

    private async Task ShowDashboardAsync(SocketMessageComponent component)
    {
        await component.UpdateAsync(
            properties =>
            {
                properties.Content = BuildDashboardText();
                properties.Components = BuildDashboardComponents((SocketGuildUser)component.User);
            });
    }

    private async Task ShowStartableServersAsync(SocketMessageComponent component)
    {
        if (!_authorization.HasPermission(
                component,
                DiscordPermissions.ServerStart))
        {
            await component.RespondAsync(
                "Your tier does not allow starting servers.",
                ephemeral: true);
            return;
        }

        var servers = GetStartableServers();
        if (servers.Count == 0)
        {
            await component.UpdateAsync(
                properties =>
                {
                    properties.Content =
                        "No stopped or faulted server profiles are currently available to start.";
                    properties.Components = BuildDashboardComponents((SocketGuildUser)component.User);
                });
            return;
        }

        await component.UpdateAsync(
            properties =>
            {
                properties.Content = "Select a server profile to start:";
                properties.Components = BuildServerSelectComponents(
                    StartSelectId,
                    "Select a server to start",
                    servers);
            });
    }

    private async Task ShowRunningServersAsync(SocketMessageComponent component)
    {
        var servers = GetRunningServers();
        if (servers.Count == 0)
        {
            await component.UpdateAsync(
                properties =>
                {
                    properties.Content = "No managed server is currently running.";
                    properties.Components = BuildDashboardComponents((SocketGuildUser)component.User);
                });
            return;
        }

        await component.UpdateAsync(
            properties =>
            {
                properties.Content = "Select a running server to manage:";
                properties.Components = BuildServerSelectComponents(
                    ManageSelectId,
                    "Select a running server",
                    servers);
            });
    }

    private async Task StartSelectedServerAsync(
        SocketMessageComponent component,
        string serverId)
    {
        if (!_authorization.HasPermission(
                component,
                DiscordPermissions.ServerStart))
        {
            await component.RespondAsync(
                "Your tier does not allow starting servers.",
                ephemeral: true);
            return;
        }

        if (!_servers.TryGetValue(serverId, out var server))
        {
            await component.RespondAsync(
                "That server profile no longer exists.",
                ephemeral: true);
            return;
        }

        if (server.State is not (ServerState.Stopped or ServerState.Faulted))
        {
            await component.UpdateAsync(
                properties =>
                {
                    properties.Content =
                        server.Profile.Name + " is currently " + server.State + ".";
                    properties.Components = BuildDashboardComponents((SocketGuildUser)component.User);
                });
            return;
        }

        await component.UpdateAsync(
            properties =>
            {
                properties.Content = "Starting " + server.Profile.Name + "...";
                properties.Components = new ComponentBuilder().Build();
            });

        _ = Task.Run(() => RunStartSelectedServerAsync(component, server));
    }

    private async Task RunStartSelectedServerAsync(
        SocketMessageComponent component,
        IGameServer server)
    {
        try
        {
            await server.StartAsync();

            await component.ModifyOriginalResponseAsync(
                properties =>
                {
                    properties.Content =
                        "Started " + server.Profile.Name +
                        ". State: " + server.State + ".\n\n" +
                        BuildDashboardText();
                    properties.Components = BuildDashboardComponents((SocketGuildUser)component.User);
                });
        }
        catch (Exception ex)
        {
            await component.ModifyOriginalResponseAsync(
                properties =>
                {
                    properties.Content =
                        "Start failed for " + server.Profile.Name +
                        ": " + ex.Message + "\n\n" +
                        BuildDashboardText();
                    properties.Components = BuildDashboardComponents((SocketGuildUser)component.User);
                });
        }
    }

    private async Task ShowServerPanelAsync(
        SocketMessageComponent component,
        string serverId)
    {
        if (!_servers.TryGetValue(serverId, out var server) ||
            server.State != ServerState.Running)
        {
            await component.UpdateAsync(
                properties =>
                {
                    properties.Content = "That server is no longer running.";
                    properties.Components = BuildDashboardComponents((SocketGuildUser)component.User);
                });
            return;
        }

        var nonce = CreateNonce();
        _panels[nonce] = new ServerPanelSession(
            component.User.Id,
            server.Profile.Id,
            DateTimeOffset.UtcNow.Add(PanelLifetime));

        await component.UpdateAsync(
            properties =>
            {
                properties.Content = BuildServerPanelText(server);
                properties.Components = BuildServerPanelComponents(
                        nonce,
                        server,
                        (SocketGuildUser)component.User);
            });
    }

    private async Task HandleServerPanelButtonAsync(
        SocketMessageComponent component,
        string action,
        string nonce)
    {
        if (action == "back")
        {
            _panels.TryRemove(nonce, out _);
            await ShowDashboardAsync(component);
            return;
        }

        if (!TryResolvePanelSession(
                component.User.Id,
                nonce,
                out var session,
                out var server))
        {
            await component.UpdateAsync(
                properties =>
                {
                    properties.Content = "This server panel has expired.";
                    properties.Components = BuildDashboardComponents((SocketGuildUser)component.User);
                });
            return;
        }

        if (action == "refresh")
        {
            await component.UpdateAsync(
                properties =>
                {
                    properties.Content = BuildServerPanelText(server);
                    properties.Components = BuildServerPanelComponents(
                        nonce,
                        server,
                        (SocketGuildUser)component.User);
                });
            return;
        }

        if (action == "players")
        {
            if (!_authorization.HasPermission(
                    component,
                    DiscordPermissions.Command("players")))
            {
                await component.RespondAsync(
                    "Your tier does not allow viewing players.",
                    ephemeral: true);
                return;
            }

            await component.DeferAsync(ephemeral: true);

            _ = Task.Run(
                () => RunProfileCommandAsync(
                    component,
                    server,
                    "players",
                    arguments: null));
            return;
        }

        if (action is not ("stop" or "restart"))
        {
            await component.RespondAsync(
                "Unsupported server action.",
                ephemeral: true);
            return;
        }

        var requiredPermission = action == "stop"
            ? DiscordPermissions.ServerStop
            : DiscordPermissions.ServerRestart;

        if (!_authorization.HasPermission(component, requiredPermission))
        {
            await component.RespondAsync(
                $"Your tier does not allow server {action}.",
                ephemeral: true);
            return;
        }

        var confirmationNonce = CreateNonce();
        _pending[confirmationNonce] = new PendingConfirmation(
            component.User.Id,
            action,
            session.ServerId,
            DateTimeOffset.UtcNow.Add(ConfirmationLifetime));

        await component.UpdateAsync(
            properties =>
            {
                properties.Content =
                    "Confirm " + action + " for " + server.Profile.Name + "?";
                properties.Components = BuildConfirmationComponents(
                    confirmationNonce);
            });
    }

    private async Task HandleConfirmationAsync(
        SocketMessageComponent component,
        string mode,
        string nonce)
    {
        if (!_pending.TryRemove(nonce, out var pending) ||
            pending.UserId != component.User.Id ||
            pending.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await component.UpdateAsync(
                properties =>
                {
                    properties.Content =
                        "This confirmation has expired or was already used.";
                    properties.Components = BuildDashboardComponents((SocketGuildUser)component.User);
                });
            return;
        }

        if (mode == "cancel")
        {
            await component.UpdateAsync(
                properties =>
                {
                    properties.Content =
                        "Cancelled " + pending.Action +
                        " for " + pending.ServerId + ".";
                    properties.Components = BuildDashboardComponents((SocketGuildUser)component.User);
                });
            return;
        }

        var requiredPermission = pending.Action == "stop"
            ? DiscordPermissions.ServerStop
            : DiscordPermissions.ServerRestart;

        if (!_authorization.HasPermission(component, requiredPermission))
        {
            await component.UpdateAsync(
                properties =>
                {
                    properties.Content =
                        "Your tier no longer allows this operation.";
                    properties.Components = BuildDashboardComponents(
                        (SocketGuildUser)component.User);
                });
            return;
        }

        if (!_servers.TryGetValue(pending.ServerId, out var server))
        {
            await component.UpdateAsync(
                properties =>
                {
                    properties.Content =
                        "The requested server is no longer available.";
                    properties.Components = BuildDashboardComponents((SocketGuildUser)component.User);
                });
            return;
        }

        await component.UpdateAsync(
            properties =>
            {
                properties.Content =
                    pending.Action + " in progress for " +
                    server.Profile.Name + "...";
                properties.Components = new ComponentBuilder().Build();
            });

        _ = Task.Run(
            () => RunConfirmedOperationAsync(
                component,
                pending,
                server));
    }

    private async Task ExecuteSelectedProfileCommandAsync(
        SocketMessageComponent component,
        string panelNonce,
        string commandName)
    {
        if (!TryResolvePanelSession(
                component.User.Id,
                panelNonce,
                out _,
                out var server))
        {
            await component.RespondAsync(
                "This server panel has expired.",
                ephemeral: true);
            return;
        }

        if (!_authorization.HasPermission(
                component,
                DiscordPermissions.Command(commandName)))
        {
            await component.RespondAsync(
                "Your tier does not allow this server command.",
                ephemeral: true);
            return;
        }

        if (!server.Profile.Commands.TryGetValue(commandName, out var template))
        {
            await component.RespondAsync(
                "That command is no longer defined by this server profile.",
                ephemeral: true);
            return;
        }

        if (template.Contains("{args}", StringComparison.Ordinal))
        {
            var promptNonce = CreateNonce();
            _commandPrompts[promptNonce] = new CommandPrompt(
                component.User.Id,
                server.Profile.Id,
                commandName,
                DateTimeOffset.UtcNow.Add(CommandPromptLifetime));

            var title = server.Profile.Name + ": " + commandName;
            if (title.Length > 45)
            {
                title = title[..45];
            }

            var modal = new ModalBuilder()
                .WithTitle(title)
                .WithCustomId("dedi:modal:" + promptNonce)
                .AddTextInput(
                    "Arguments",
                    "arguments",
                    TextInputStyle.Paragraph,
                    placeholder: Truncate("Arguments for " + commandName, 100),
                    maxLength: 4000,
                    required: true)
                .Build();

            await component.RespondWithModalAsync(modal);
            return;
        }

        await component.DeferAsync(ephemeral: true);

        _ = Task.Run(
            () => RunProfileCommandAsync(
                component,
                server,
                commandName,
                arguments: null));
    }

    private static async Task RunProfileCommandAsync(
        SocketInteraction interaction,
        IGameServer server,
        string commandName,
        string? arguments)
    {
        try
        {
            var result = await server.ExecuteAsync(commandName, arguments);

            await interaction.ModifyOriginalResponseAsync(
                properties =>
                {
                    properties.Content = FormatCommandResult(
                        server,
                        commandName,
                        result);
                });
        }
        catch (Exception ex)
        {
            await interaction.ModifyOriginalResponseAsync(
                properties =>
                {
                    properties.Content = "Command failed: " + ex.Message;
                });
        }
    }

    private MessageComponent BuildDashboardComponents(SocketGuildUser user)
    {
        var canStart = _authorization.HasPermission(
            user,
            DiscordPermissions.ServerStart);
        var hasStartable = GetStartableServers().Count > 0;
        var hasRunning = GetRunningServers().Count > 0;

        return new ComponentBuilder()
            .WithButton(
                "Start Server",
                DashboardStartId,
                ButtonStyle.Success,
                disabled: !canStart || !hasStartable,
                row: 0)
            .WithButton(
                "Manage Running",
                DashboardManageId,
                ButtonStyle.Primary,
                disabled: !hasRunning,
                row: 0)
            .WithButton(
                "Refresh",
                DashboardRefreshId,
                ButtonStyle.Secondary,
                row: 0)
            .Build();
    }

    private static MessageComponent BuildServerSelectComponents(
        string customId,
        string placeholder,
        IReadOnlyList<IGameServer> servers)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId(customId)
            .WithPlaceholder(placeholder)
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var server in servers
                     .Where(item => item.Profile.Id.Length <= 100)
                     .Take(25))
        {
            menu.AddOption(
                Truncate(
                    server.Profile.Name + " (" + server.Profile.Id + ")",
                    100),
                server.Profile.Id,
                Truncate("State: " + server.State, 100));
        }

        return new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton(
                "Back",
                DashboardRefreshId,
                ButtonStyle.Secondary,
                row: 1)
            .Build();
    }

    private MessageComponent BuildServerPanelComponents(
        string nonce,
        IGameServer server,
        SocketGuildUser user)
    {
        var canRestart = _authorization.HasPermission(
            user,
            DiscordPermissions.ServerRestart);
        var canStop = _authorization.HasPermission(
            user,
            DiscordPermissions.ServerStop);

        var builder = new ComponentBuilder();

        if (canRestart)
        {
            builder.WithButton(
                "Restart",
                "dedi:panel:restart:" + nonce,
                ButtonStyle.Primary,
                disabled: server.State != ServerState.Running,
                row: 0);
        }

        if (canStop)
        {
            builder.WithButton(
                "Stop",
                "dedi:panel:stop:" + nonce,
                ButtonStyle.Danger,
                disabled: server.State != ServerState.Running,
                row: 0);
        }

        var canViewPlayers =
            server.Profile.Commands.ContainsKey("players") &&
            _authorization.HasPermission(
                user,
                DiscordPermissions.Command("players"));

        if (canViewPlayers)
        {
            builder.WithButton(
                "Players",
                "dedi:panel:players:" + nonce,
                ButtonStyle.Secondary,
                disabled: server.State != ServerState.Running,
                row: 0);
        }

        builder
            .WithButton(
                "Refresh",
                "dedi:panel:refresh:" + nonce,
                ButtonStyle.Secondary,
                row: 0)
            .WithButton(
                "Back",
                "dedi:panel:back:" + nonce,
                ButtonStyle.Secondary,
                row: 0);

        var permittedCommands = server.Profile.Commands
            .Where(
                pair => _authorization.HasPermission(
                    user,
                    DiscordPermissions.Command(pair.Key)))
            .ToList();

        if (permittedCommands.Count > 0 &&
            server.State == ServerState.Running)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId("dedi:commands:" + nonce)
                .WithPlaceholder("Run a server command")
                .WithMinValues(1)
                .WithMaxValues(1);

            foreach (var command in permittedCommands
                         .Where(pair => pair.Key.Length <= 100)
                         .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(25))
            {
                var requiresArguments = command.Value.Contains(
                    "{args}",
                    StringComparison.Ordinal);

                menu.AddOption(
                    Truncate(command.Key, 100),
                    command.Key,
                    requiresArguments
                        ? "Requires arguments - opens a form"
                        : Truncate(command.Value, 100));
            }

            builder.WithSelectMenu(menu, row: 1);
        }

        return builder.Build();
    }

    private static MessageComponent BuildConfirmationComponents(string nonce) =>
        new ComponentBuilder()
            .WithButton(
                "Confirm",
                "dedi:confirm:" + nonce,
                ButtonStyle.Danger,
                row: 0)
            .WithButton(
                "Cancel",
                "dedi:cancel:" + nonce,
                ButtonStyle.Secondary,
                row: 0)
            .Build();

    private string BuildDashboardText()
    {
        var ordered = _servers.Values
            .OrderBy(server => server.Profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var running = ordered.Count(
            server => server.State == ServerState.Running);
        var available = ordered.Count(
            server => server.State is ServerState.Stopped or ServerState.Faulted);

        var lines = ordered
            .Take(20)
            .Select(
                server => Truncate(
                    "- " + server.Profile.Name +
                    " (" + server.Profile.Id + "): " + server.State,
                    120))
            .ToList();

        if (ordered.Count > lines.Count)
        {
            lines.Add(
                "- ...and " + (ordered.Count - lines.Count) +
                " more profile(s)");
        }

        return
            "OM_DEDI Control Panel\n" +
            "Running: " + running +
            " | Available to start: " + available +
            " | Total: " + ordered.Count +
            "\n\n" +
            string.Join(Environment.NewLine, lines);
    }

    private static string BuildServerPanelText(IGameServer server)
    {
        var pid = server.ProcessId?.ToString() ?? "-";
        var uptime = server.StartedAt is null
            ? "-"
            : FormatDuration(DateTimeOffset.UtcNow - server.StartedAt.Value);

        return
            server.Profile.Name + "\n" +
            "ID: " + server.Profile.Id + "\n" +
            "State: " + server.State + "\n" +
            "PID: " + pid + "\n" +
            "Uptime: " + uptime + "\n" +
            "Transport: " + server.Profile.Transport.Type;
    }

    private IReadOnlyList<IGameServer> GetStartableServers() =>
        _servers.Values
            .Where(
                server =>
                    server.State is ServerState.Stopped or ServerState.Faulted)
            .OrderBy(
                server => server.Profile.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

    private IReadOnlyList<IGameServer> GetRunningServers() =>
        _servers.Values
            .Where(server => server.State == ServerState.Running)
            .OrderBy(
                server => server.Profile.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

    private bool TryResolvePanelSession(
        ulong userId,
        string nonce,
        out ServerPanelSession session,
        out IGameServer server)
    {
        server = null!;

        if (!_panels.TryGetValue(nonce, out var resolvedSession) ||
            resolvedSession.UserId != userId ||
            resolvedSession.ExpiresAt <= DateTimeOffset.UtcNow ||
            !_servers.TryGetValue(
                resolvedSession.ServerId,
                out var resolvedServer))
        {
            session = null!;
            _panels.TryRemove(nonce, out _);
            return false;
        }

        session = resolvedSession;
        server = resolvedServer;
        return true;
    }

    private void CleanupExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var pair in _pending)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _pending.TryRemove(pair.Key, out _);
            }
        }

        foreach (var pair in _panels)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _panels.TryRemove(pair.Key, out _);
            }
        }

        foreach (var pair in _commandPrompts)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _commandPrompts.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string CreateNonce() => Guid.NewGuid().ToString("N");

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..maxLength];

    private static string FormatCommandResult(
        IGameServer server,
        string commandName,
        CommandResult result)
    {
        if (!result.Succeeded)
        {
            return "Command failed: " + result.Error;
        }

        if (result.Output is null || result.Output.Count == 0)
        {
            return "Sent " + commandName + " to " +
                server.Profile.Name + ". No console output was captured.";
        }

        var output = string.Join(
            Environment.NewLine,
            result.Output
                .Select(item => item.Line)
                .Where(line => !string.IsNullOrWhiteSpace(line)));

        if (string.IsNullOrWhiteSpace(output))
        {
            return "Sent " + commandName + " to " +
                server.Profile.Name + ". No console output was captured.";
        }

        output = SanitizeDiscordOutput(output);
        output = output.Replace("```", "'''", StringComparison.Ordinal);
        output = Truncate(output, 1700);

        var heading = string.Equals(
            commandName,
            "players",
            StringComparison.OrdinalIgnoreCase)
            ? "Players on " + server.Profile.Name
            : server.Profile.Name + " - " + commandName;

        return heading + ":\n```text\n" + output + "\n```";
    }

    private static string SanitizeDiscordOutput(string output) =>
        NetworkEndpointRegex.Replace(output, "[endpoint hidden]");

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return
                ((int)value.TotalDays) + "d " +
                value.Hours + "h " +
                value.Minutes + "m";
        }

        if (value.TotalHours >= 1)
        {
            return
                ((int)value.TotalHours) + "h " +
                value.Minutes + "m";
        }

        return Math.Max(0, (int)value.TotalMinutes) + "m";
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

    private sealed record ServerPanelSession(
        ulong UserId,
        string ServerId,
        DateTimeOffset ExpiresAt);

    private sealed record CommandPrompt(
        ulong UserId,
        string ServerId,
        string CommandName,
        DateTimeOffset ExpiresAt);
}
