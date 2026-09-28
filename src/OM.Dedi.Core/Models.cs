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

public sealed record CommandResult(
    bool Succeeded,
    string? Error = null,
    IReadOnlyList<ServerOutput>? Output = null)
{
    public static CommandResult Success(
        IReadOnlyList<ServerOutput>? output = null) =>
        new(true, Output: output);

    public static CommandResult Failure(string error) =>
        new(false, error);
}
