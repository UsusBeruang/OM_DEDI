# OM_DEDI

**OM_DEDI** — **Orchestration Manager for Dedicated Servers**

A generic .NET 8 control layer for running and remotely administering dedicated game servers.

## Goals

- Keep game-server lifecycle, command transport, and remote-control frontends independent.
- Support local processes first, with stdin/stdout as the first command transport.
- Add transports such as RCON, TCP, Docker exec, named pipes, or HTTP without changing frontends.
- Let Discord, desktop UI, CLI, and future web clients control the same server runtime.
- Model game-specific commands as configuration rather than hardcoded Discord commands.

## Initial architecture

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

## Repository layout

```text
src/
  OM.Dedi.Core/      Domain abstractions and configuration
  OM.Dedi.Runtime/   Process supervision and transports
  OM.Dedi.Host/      Executable host / local control surface
examples/
  romestead.json     Example stdin-based server profile
```

## Status

Bootstrap phase. The first milestone is local process supervision + stdin/stdout command control, which is enough to control servers such as Romestead without RCON.

Discord integration comes next and will consume the same abstractions rather than owning the server process directly.
