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
- optional Discord control panel launched by a single `/dedi` command;
- mouse-driven server start/manage flows using buttons and select menus;
- Discord user/role allowlists with default-deny behavior;
- confirmation buttons for remote stop/restart;
- profile-defined command picker with modals for commands that require arguments;
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

Then run OM_DEDI normally. The bot registers one guild-scoped `/dedi` command.

Running `/dedi` opens an ephemeral control panel. From there you can:

- start stopped/faulted server profiles from a dropdown;
- select and manage currently running servers;
- restart or stop a server with confirmation;
- refresh server state and view PID/uptime/transport information;
- run commands explicitly declared in the selected server profile;
- fill in a Discord modal when a profile command contains `{args}`.

Remote raw console execution is intentionally unavailable.

## Next

- structured response capture for commands such as player listing;
- test coverage for lifecycle, command expansion, authorization, and dashboard interactions;
- RCON transport without changing the Discord or CLI contract;
- server-event parsing and Discord notifications.
