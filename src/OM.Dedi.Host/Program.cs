using System.Text.Json;
using OM.Dedi.Core;
using OM.Dedi.Discord;
using OM.Dedi.Runtime;

var profileDirectory = args.Length > 0
    ? args[0]
    : Path.Combine(Environment.CurrentDirectory, "examples");

if (!Directory.Exists(profileDirectory))
{
    Console.Error.WriteLine(
        $"Profile directory does not exist: {profileDirectory}");
    return 1;
}

var serializerOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true
};

var servers = new Dictionary<string, IGameServer>(
    StringComparer.OrdinalIgnoreCase);

foreach (var path in Directory.EnumerateFiles(
             profileDirectory,
             "*.json",
             SearchOption.TopDirectoryOnly))
{
    await using var stream = File.OpenRead(path);
    var profile = await JsonSerializer.DeserializeAsync<ServerProfile>(
        stream,
        serializerOptions);

    if (profile is null)
    {
        continue;
    }

    var server = GameServerFactory.Create(profile);
    server.OutputReceived += output =>
    {
        var prefix = output.Stream == ServerOutputStream.StandardError
            ? "ERR"
            : "OUT";
        Console.WriteLine(
            $"[{DateTimeOffset.Now:HH:mm:ss}] [{profile.Id}] [{prefix}] {output.Line}");
    };

    servers.Add(profile.Id, server);
}

if (servers.Count == 0)
{
    Console.Error.WriteLine(
        $"No server profiles found in: {profileDirectory}");
    return 1;
}

Console.WriteLine("OM_DEDI — Orchestration Manager for Dedicated Servers");
Console.WriteLine($"Loaded {servers.Count} server profile(s).");

DiscordControlService? discord = null;

try
{
    discord = await TryStartDiscordAsync(
        servers,
        serializerOptions);
}
catch (Exception ex)
{
    Console.Error.WriteLine(
        $"Discord control failed to start: {ex.Message}");
    await StopAllAsync(servers.Values);
    return 1;
}

PrintHelp();

while (true)
{
    Console.Write("om_dedi> ");
    var line = Console.ReadLine();

    if (line is null)
    {
        break;
    }

    var parts = line.Split(
        ' ',
        4,
        StringSplitOptions.RemoveEmptyEntries |
        StringSplitOptions.TrimEntries);

    if (parts.Length == 0)
    {
        continue;
    }

    try
    {
        switch (parts[0].ToLowerInvariant())
        {
            case "help":
                PrintHelp();
                break;

            case "servers":
                foreach (var server in servers.Values)
                {
                    PrintStatus(server);
                }

                break;

            case "status":
                if (!TryGetServer(parts, servers, out var statusServer))
                {
                    break;
                }

                PrintStatus(statusServer);
                break;

            case "start":
                if (!TryGetServer(parts, servers, out var startServer))
                {
                    break;
                }

                await startServer.StartAsync();
                PrintStatus(startServer);
                break;

            case "stop":
                if (!TryGetServer(parts, servers, out var stopServer))
                {
                    break;
                }

                await stopServer.StopAsync();
                PrintStatus(stopServer);
                break;

            case "restart":
                if (!TryGetServer(parts, servers, out var restartServer))
                {
                    break;
                }

                await restartServer.RestartAsync();
                PrintStatus(restartServer);
                break;

            case "exec":
                if (parts.Length < 3 ||
                    !servers.TryGetValue(parts[1], out var execServer))
                {
                    Console.WriteLine(
                        "Usage: exec <server> <command> [arguments]");
                    break;
                }

                var execArguments = parts.Length == 4 ? parts[3] : null;
                PrintResult(
                    await execServer.ExecuteAsync(
                        parts[2],
                        execArguments));
                break;

            case "send":
                if (parts.Length < 3 ||
                    !servers.TryGetValue(parts[1], out var rawServer))
                {
                    Console.WriteLine(
                        "Usage: send <server> <raw command>");
                    break;
                }

                var rawCommand = parts.Length == 4
                    ? $"{parts[2]} {parts[3]}"
                    : parts[2];

                PrintResult(await rawServer.SendRawAsync(rawCommand));
                break;

            case "quit":
            case "exit":
                await ShutdownAsync(servers.Values, discord);
                return 0;

            default:
                Console.WriteLine(
                    "Unknown command. Type 'help' for available commands.");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
    }
}

await ShutdownAsync(servers.Values, discord);
return 0;

static async Task<DiscordControlService?> TryStartDiscordAsync(
    IReadOnlyDictionary<string, IGameServer> servers,
    JsonSerializerOptions serializerOptions)
{
    var token = Environment.GetEnvironmentVariable(
        "OM_DEDI_DISCORD_TOKEN");

    if (string.IsNullOrWhiteSpace(token))
    {
        Console.WriteLine(
            "Discord control disabled (OM_DEDI_DISCORD_TOKEN is not set).");
        return null;
    }

    var configPath = Environment.GetEnvironmentVariable(
        "OM_DEDI_DISCORD_CONFIG");

    if (string.IsNullOrWhiteSpace(configPath))
    {
        configPath = Path.Combine(
            Environment.CurrentDirectory,
            "discord.json");
    }

    if (!File.Exists(configPath))
    {
        throw new FileNotFoundException(
            "Discord token is configured, but the Discord config file was not found.",
            configPath);
    }

    await using var stream = File.OpenRead(configPath);
    var options = await JsonSerializer.DeserializeAsync<DiscordOptions>(
        stream,
        serializerOptions);

    if (options is null)
    {
        throw new InvalidOperationException(
            $"Unable to parse Discord config: {configPath}");
    }

    var service = new DiscordControlService(
        token,
        options,
        servers);

    try
    {
        await service.StartAsync();
        Console.WriteLine(
            $"Discord control enabled for guild {options.GuildId}.");
        return service;
    }
    catch
    {
        await service.DisposeAsync();
        throw;
    }
}

static bool TryGetServer(
    string[] parts,
    IReadOnlyDictionary<string, IGameServer> servers,
    out IGameServer server)
{
    if (parts.Length >= 2 &&
        servers.TryGetValue(parts[1], out var resolvedServer))
    {
        server = resolvedServer;
        return true;
    }

    server = null!;
    Console.WriteLine("Specify a valid server id.");
    return false;
}

static void PrintStatus(IGameServer server)
{
    var pid = server.ProcessId?.ToString() ?? "-";
    Console.WriteLine(
        $"{server.Profile.Id,-18} {server.State,-10} PID={pid}  {server.Profile.Name}");
}

static void PrintResult(CommandResult result)
{
    Console.WriteLine(
        result.Succeeded
            ? "Command sent."
            : $"Command failed: {result.Error}");
}

static async Task ShutdownAsync(
    IEnumerable<IGameServer> servers,
    DiscordControlService? discord)
{
    if (discord is not null)
    {
        await discord.DisposeAsync();
    }

    await StopAllAsync(servers);
}

static async Task StopAllAsync(IEnumerable<IGameServer> servers)
{
    foreach (var server in servers.Where(
                 server => server.State != ServerState.Stopped))
    {
        try
        {
            await server.StopAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Failed to stop '{server.Profile.Id}': {ex.Message}");
        }
    }
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        Commands:
          servers
          status  <server>
          start   <server>
          stop    <server>
          restart <server>
          exec    <server> <profile-command> [arguments]
          send    <server> <raw command>
          help
          quit
        """);
}
