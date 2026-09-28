namespace OM.Dedi.Core;

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}

public enum ServerOutputStream
{
    StandardOutput,
    StandardError
}

public sealed record ServerOutput(
    DateTimeOffset Timestamp,
    ServerOutputStream Stream,
    string Line);

public sealed record CommandResult(bool Succeeded, string? Error = null)
{
    public static CommandResult Success() => new(true);

    public static CommandResult Failure(string error) => new(false, error);
}
