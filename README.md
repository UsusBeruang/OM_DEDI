# OM_DEDI

**OM_DEDI** — **Orchestration Manager for Dedicated Servers**

A generic .NET 8 control layer for running and remotely administering dedicated game servers.

## Design

OM_DEDI deliberately separates three concerns:

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

A frontend does not need to know whether a game uses stdin, RCON, Docker exec, TCP, HTTP, or another transport. Likewise, game-specific command names live in profiles rather than in Discord commands.

## Current milestone

The first runnable slice supports:

- local dedicated-server process start / stop / restart;
- redirected stdin command delivery;
- streamed stdout and stderr;
- graceful stop command with forced process-tree termination as fallback;
- named, profile-defined commands such as `players`, `save`, and `say`;
- raw console commands for local development;
- multiple server profiles loaded by one host process.

Discord is intentionally the next layer, not part of the runtime.

## Repository layout

```text
src/
  OM.Dedi.Core/      Domain abstractions and server profiles
  OM.Dedi.Runtime/   Process supervision and command transports
  OM.Dedi.Host/      Local CLI host
examples/
  romestead.json     Example stdin-based server profile
```

## Build

Requires the .NET 8 SDK.

```powershell
dotnet build OM_DEDI.sln
```

## Run the local host

By default the host loads every JSON profile in `./examples`:

```powershell
dotnet run --project src/OM.Dedi.Host
```

Or point it at your own profile directory:

```powershell
dotnet run --project src/OM.Dedi.Host -- E:\_servers_\om_dedi\profiles
```

The interactive shell currently provides:

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

Example:

```text
start romestead
exec romestead players
exec romestead say Server restart in 5 minutes
exec romestead save
stop romestead
```

## Profiles

`examples/romestead.json` demonstrates a stdin-controlled server. Copy it to your own profile directory and adjust the executable and working directory.

The raw `send` command is currently a development surface. Remote frontends such as Discord should expose an allow-listed capability model and enforce authorization rather than forwarding arbitrary console text.

## Next

1. Add automated tests for process lifecycle and command expansion.
2. Add a Discord adapter with guild/user/role authorization.
3. Add confirmation handling for destructive commands.
4. Add structured command-response capture for commands such as player listing.
5. Add additional transports such as RCON without changing the frontend contract.
