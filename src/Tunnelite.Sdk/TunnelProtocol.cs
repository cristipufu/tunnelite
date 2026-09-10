namespace Tunnelite.Sdk;

internal static class TunnelProtocol
{
    /// <summary>
    /// Largest payload placed in a single SignalR stream item. A server rejects hub messages above its
    /// <c>MaximumReceiveMessageSize</c> by closing the whole connection, so chunks stay well below the
    /// 32 KB default even before the server raised it.
    /// </summary>
    public const int ChunkSize = 16 * 1024;
}
