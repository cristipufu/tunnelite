using System.Collections.Concurrent;

namespace Tunnelite.Client;

internal sealed record CliError(long Sequence, string Message);
internal sealed record ActivitySnapshot(long Requests, long Failures, string Latest, CliError[] Errors);

/// <summary>Request callbacks do bounded in-memory work; only the renderer writes terminal output.</summary>
internal sealed class CliActivity
{
    internal const int RecentErrorLimit = 3;
    private readonly ConcurrentQueue<CliError> _errors = new();
    private long _requests, _failures, _sequence, _revision;
    private string _latest = "Waiting for requests";
    internal long Revision => Volatile.Read(ref _revision);

    internal void Request(string method, string path)
    {
        Volatile.Write(ref _latest, TerminalText.Request(method, path));
        Interlocked.Increment(ref _requests);
        Interlocked.Increment(ref _revision);
    }

    internal void Failure(string method, string path)
    {
        Interlocked.Increment(ref _failures);
        Error("Failed: " + TerminalText.Request(method, path));
    }

    internal void Error(string message)
    {
        _errors.Enqueue(new(Interlocked.Increment(ref _sequence), TerminalText.Clean(message, 240)));
        while (_errors.Count > RecentErrorLimit) _errors.TryDequeue(out _);
        Interlocked.Increment(ref _revision);
    }

    internal ActivitySnapshot Snapshot() => new(Volatile.Read(ref _requests), Volatile.Read(ref _failures),
        Volatile.Read(ref _latest), _errors.ToArray());

    internal void Clear()
    {
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _failures, 0);
        Volatile.Write(ref _latest, "Waiting for requests");
        _errors.Clear();
        Interlocked.Increment(ref _revision);
    }
}
