namespace Tunnelite.Server;

public static class TunnelProtocol
{
    /// <summary>
    /// Largest payload placed in a single SignalR stream item. Kept well under
    /// <see cref="Microsoft.AspNetCore.SignalR.HubOptions.MaximumReceiveMessageSize"/> so that a chunk plus its
    /// MessagePack envelope never trips the limit. Mirrored in <c>Tunnelite.Sdk</c>.
    /// </summary>
    public const int ChunkSize = 16 * 1024;
}
