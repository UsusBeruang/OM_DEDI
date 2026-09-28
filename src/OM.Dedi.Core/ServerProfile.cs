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
}

public sealed record TransportProfile
{
    public string Type { get; init; } = "stdin";
}
