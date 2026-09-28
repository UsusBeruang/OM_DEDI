namespace OM.Dedi.Core;

public sealed record ServerProfile
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required ProcessProfile Process { get; init; }

    public TransportProfile Transport { get; init; } = new();

    public string? StopCommand { get; init; }

    public int ShutdownTimeoutSeconds { get; init; } = 15;

    public Dictionary<string, string> Commands { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, CommandCaptureProfile> CommandCaptures { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed record CommandCaptureProfile
{
    public int TimeoutMs { get; init; } = 2500;

    public int QuietPeriodMs { get; init; } = 400;

    public bool IncludeStandardError { get; init; }
}

public sealed record ProcessProfile
{
    public required string Executable { get; init; }

    public string WorkingDirectory { get; init; } = ".";

    public string Arguments { get; init; } = string.Empty;

    public string Mode { get; init; } = "redirected";

    public int TerminalColumns { get; init; } = 120;

    public int TerminalRows { get; init; } = 30;

    // Input terminator: "auto", "cr", "lf", or "crlf".
    // "auto" uses the platform newline for redirected stdin and CR for PTY.
    public string InputTerminator { get; init; } = "auto";

    // Emits a local diagnostic event whenever OM_DEDI writes to the server input stream.
    public bool TraceInput { get; init; }

    // Keep the Windows console attached by default. Some dedicated servers
    // accept redirected stdin only while a console is present.
    public bool CreateNoWindow { get; init; } = false;

    // Remove inherited environment variables before launching the server process.
    public List<string> RemoveEnvironmentVariables { get; init; } = [];

    // Explicit environment overrides applied after removals.
    public Dictionary<string, string> EnvironmentVariables { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed record TransportProfile
{
    public string Type { get; init; } = "stdin";
}
