# OM_DEDI

**OM_DEDI** — **Orchestration Manager for Dedicated Servers**

A generic .NET 8 control layer for running and remotely administering dedicated game servers.

## Design

OM_DEDI separates server lifecycle, command transport, and control surfaces:

```text
Discord / CLI / Desktop / Web
            |
        OM_DEDI Host
            |
      IGameServer
       /       \
IServerProcess  ICommandTransport
     |                |
LocalProcess      StdinTransport
```

A frontend does not need to know whether a game uses stdin, RCON, Docker exec, TCP, HTTP, or another transport. Game-specific command names live in profiles rather than in Discord commands.

## Current features

- multiple profile-driven dedicated servers;
- local process start / stop / restart;
- redirected stdin command delivery;
- streamed stdout and stderr;
- graceful stop command with forced process-tree termination fallback;
- named server-profile commands;
- local raw console access for development;
- optional Discord slash-command control;
- Discord user/role allowlists with default-deny behavior;
- confirmation buttons for remote stop/restart;
- no raw Discord console command.

## Repository layout

```text
src/
  OM.Dedi.Core/      Domain abstractions and server profiles
  OM.Dedi.Runtime/   Process supervision and command transports
  OM.Dedi.Discord/   Discord remote-control adapter
  OM.Dedi.Host/      Local CLI host and composition root
examples/
  romestead.json     Example stdin-based server profile
discord.example.json
```

## Build

Requires the .NET 8 SDK.

```powershell
dotnet build OM_DEDI.sln
```

## Local host

By default the host loads every JSON server profile in `./examples`:

```powershell
dotnet run --project src/OM.Dedi.Host
```

Or supply a profile directory:

```powershell
dotnet run --project src/OM.Dedi.Host -- E:\_servers_\om_dedi\profiles
```

Local commands:

```text
servers
status  <server>
start   <server>
stop    <server>
restart <server>
exec    <server> <profile-command> [arguments]
send    <server> <raw command>
help
quit
```

The local `send` command intentionally remains a development/admin surface. It is not exposed through Discord.

## Discord setup

1. Create a Discord application/bot and invite it to the guild where you want to administer servers.
2. Copy `discord.example.json` to `discord.json`.
3. Set the guild ID and at least one allowed Discord user ID or role ID.
4. Set the bot token only through the environment:

```powershell
$env:OM_DEDI_DISCORD_TOKEN = "your-bot-token"
```

Optionally point to a different config file:

```powershell
$env:OM_DEDI_DISCORD_CONFIG = "E:\_servers_\om_dedi\discord.json"
```

Then run OM_DEDI normally. The bot registers a guild-scoped `/dedi` command.

Available Discord subcommands:

```text
/dedi servers
/dedi status  server:<id>
/dedi start   server:<id>
/dedi stop    server:<id>
/dedi restart server:<id>
/dedi exec    server:<id> command:<profile-command> arguments:<optional>
```

`stop` and `restart` require confirmation. `exec` can only invoke commands explicitly declared in that server's profile. Remote raw console execution is intentionally unavailable.

## Next

- structured response capture for commands such as player listing;
- server and command autocomplete in Discord;
- test coverage for lifecycle, command expansion, and authorization;
- RCON transport without changing the Discord or CLI contract;
- server-event parsing and Discord notifications.
