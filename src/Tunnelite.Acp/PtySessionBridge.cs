using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Acplite.Pty;
using Acplite.Sdk.Protocol;
using Acplite.Sdk.Runtime;

namespace Tunnelite.Acp;

/// <summary>
/// Connects <see cref="Acplite.Sdk.AcpliteRuntime"/> to local interactive terminal sessions
/// managed by <c>Acplite.Pty</c> so mobile clients can see and approve terminal tool prompts
/// and inject messages into open terminal tabs.
/// </summary>
public sealed class PtySessionBridge(string? registryDirectory = null) : IAcpExternalSessionBridge
{
    public static PtySessionBridge Instance { get; } = new();

    public async Task<bool> HasActiveSessionAsync(
        AgentProviderKind provider,
        string? sessionId,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var matched = await PtySessionRegistry.FindMatchingSessionAsync(
            provider.ToShortId(),
            sessionId,
            workingDirectory,
            registryDirectory,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return matched is not null;
    }

    public async IAsyncEnumerable<AcpEvent> MonitorSessionEventsAsync(
        AcpDiscoveredSession session,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var shortId = session.Provider.ToShortId();
        var channel = Channel.CreateUnbounded<AcpEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pumpTask = PumpEventsLoopAsync(session, shortId, channel.Writer, linkedCts.Token);
        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(linkedCts.Token).ConfigureAwait(false))
            {
                yield return ev;
            }
        }
        finally
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            try { await pumpTask.WaitAsync(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false); } catch { }
        }
    }

    private async Task PumpEventsLoopAsync(
        AcpDiscoveredSession session,
        string shortId,
        ChannelWriter<AcpEvent> writer,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                PtyControlClient? client = null;
                try
                {
                    client = await PtyControlClient.TryConnectToSessionAsync(
                        shortId,
                        session.SessionId,
                        session.WorkingDirectory,
                        session.TranscriptPath,
                        registryDirectory,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (client is null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    string? lastPublishedPermId = null;
                    var initialPerm = client.ActivePermission;
                    if (initialPerm is not null)
                    {
                        lastPublishedPermId = initialPerm.RequestId;
                        writer.TryWrite(MapPermissionEvent(session.Provider, session.SessionId, initialPerm));
                    }

                    await foreach (var ev in client.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (ev.Type == PtyControlEventTypes.PermissionRequest && ev.Permission is not null)
                        {
                            if (!string.Equals(lastPublishedPermId, ev.Permission.RequestId, StringComparison.Ordinal))
                            {
                                lastPublishedPermId = ev.Permission.RequestId;
                                writer.TryWrite(MapPermissionEvent(session.Provider, session.SessionId, ev.Permission));
                            }
                        }
                        else if (ev.Type == PtyControlEventTypes.PermissionResolved)
                        {
                            lastPublishedPermId = null;
                            writer.TryWrite(CreateResolvedPermissionEvent(session.Provider, session.SessionId, ev.RequestId));
                        }
                        else if (ev.Type == PtyControlEventTypes.Exited)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Retry after delay
                }
                finally
                {
                    if (client is not null)
                    {
                        await client.DisposeAsync().ConfigureAwait(false);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        finally
        {
            writer.TryComplete();
        }
    }

    public async Task<IAcpExternalSessionChannel?> TryConnectChannelAsync(
        AgentProviderKind provider,
        string sessionId,
        string workingDirectory,
        string? transcriptPath = null,
        CancellationToken cancellationToken = default)
    {
        var client = await PtyControlClient.TryConnectToSessionAsync(
            provider.ToShortId(),
            sessionId,
            workingDirectory,
            transcriptPath,
            registryDirectory,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return client is null ? null : new PtyExternalSessionChannel(provider, sessionId, client);
    }

    internal static AcpPermissionRequestEvent MapPermissionEvent(
        AgentProviderKind provider,
        string sessionId,
        PtyPendingPermission perm)
    {
        var options = perm.Options
            .Select(static o => new AcpPermissionOption(o.OptionId, o.Name, o.Kind))
            .ToList();

        return new AcpPermissionRequestEvent(
            Provider: provider,
            SessionId: sessionId,
            Timestamp: perm.DetectedAt,
            RequestId: perm.RequestId,
            ToolName: perm.ToolName,
            Title: perm.Title,
            Description: perm.Description,
            RawInputJson: perm.RawInputJson,
            Options: options);
    }

    internal static AcpPermissionRequestEvent CreateResolvedPermissionEvent(
        AgentProviderKind provider,
        string sessionId,
        string? requestId)
        => new(
            Provider: provider,
            SessionId: sessionId,
            Timestamp: DateTimeOffset.UtcNow,
            RequestId: requestId ?? string.Empty,
            ToolName: string.Empty,
            Title: string.Empty,
            Description: null,
            RawInputJson: null,
            Options: []);

    private sealed class PtyExternalSessionChannel(
        AgentProviderKind provider,
        string sessionId,
        PtyControlClient client) : IAcpExternalSessionChannel
    {
        public int ProcessId => client.LatestMetadata?.Pid ?? 0;

        public async IAsyncEnumerable<AcpEvent> ReadControlEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string? lastPublishedPermId = null;
            var initialPerm = client.ActivePermission;
            if (initialPerm is not null)
            {
                lastPublishedPermId = initialPerm.RequestId;
                yield return MapPermissionEvent(provider, sessionId, initialPerm);
            }

            await foreach (var ev in client.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (ev.Type == PtyControlEventTypes.PermissionRequest && ev.Permission is not null)
                {
                    if (!string.Equals(lastPublishedPermId, ev.Permission.RequestId, StringComparison.Ordinal))
                    {
                        lastPublishedPermId = ev.Permission.RequestId;
                        yield return MapPermissionEvent(provider, sessionId, ev.Permission);
                    }
                }
                else if (ev.Type == PtyControlEventTypes.PermissionResolved)
                {
                    lastPublishedPermId = null;
                    yield return CreateResolvedPermissionEvent(provider, sessionId, ev.RequestId);
                }
                else if (ev.Type == PtyControlEventTypes.Exited)
                {
                    yield break;
                }
            }
        }

        public Task SendPromptAsync(string prompt, CancellationToken cancellationToken = default)
            => client.SendPromptAsync(prompt, cancellationToken);

        public Task SteerAsync(string prompt, CancellationToken cancellationToken = default)
            => client.SteerAsync(prompt, cancellationToken);

        public Task RespondPermissionAsync(
            string requestId,
            bool approved,
            string? selectedOptionId = null,
            CancellationToken cancellationToken = default)
            => client.RespondPermissionAsync(requestId, approved, selectedOptionId, cancellationToken);

        public Task CancelTurnAsync(CancellationToken cancellationToken = default)
            => client.CancelTurnAsync(cancellationToken);

        public ValueTask DisposeAsync()
            => client.DisposeAsync();
    }
}
