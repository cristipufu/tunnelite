using System.Text.Json.Serialization;

namespace Tunnelite.Sdk;

/// <summary>
/// Source generated serializer for the tunnel registration call, so the client does not need
/// reflection-based JSON at runtime.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HttpTunnelRequest))]
[JsonSerializable(typeof(HttpTunnelResponse))]
internal partial class TunneliteJsonContext : JsonSerializerContext
{
}
