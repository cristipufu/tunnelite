using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tunnelite.Sdk;

namespace Tunnelite.Client;

// This file and its lease exist exclusively on the desktop running the CLI.
internal sealed class DesktopProfile : IAsyncDisposable
{
    private readonly string _path;
    private readonly FileStream _lease;
    private ProfileData _data;
    public TunnelIdentity Identity { get; }
    public PairedAccessState Access => _data.Access;

    private DesktopProfile(string path, FileStream lease, ProfileData data)
    {
        _path = path;
        _lease = lease;
        _data = data;
        Identity = new(data.Secret);
    }

    internal static async Task<DesktopProfile> OpenAsync(string? path, string name, string relay, CancellationToken cancellationToken)
    {
        if (name.Length is < 1 or > 64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("A profile name may contain 1–64 letters, digits, underscores or hyphens.");
        var origin = new Uri(relay).GetLeftPart(UriPartial.Authority);
        var relayId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin))).ToLowerInvariant()[..16];
        path = Path.GetFullPath(path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tunnelite", $"{relayId}-{name}.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream lease;
        try { lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new IOException("This desktop profile is already open by another Tunnelite process.", exception); }
        try
        {
            ProfileData data;
            if (File.Exists(path))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                data = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path, cancellationToken), CliJsonContext.Default.ProfileData)
                    ?? throw new InvalidDataException("The desktop profile is empty.");
                if (data.Version != 1 || data.RelayOrigin != origin)
                    throw new InvalidDataException("This profile belongs to a different relay or profile format.");
            }
            else data = new() { Secret = TunnelIdentity.Create().Secret, RelayOrigin = origin };
            var profile = new DesktopProfile(path, lease, data);
            await profile.SaveAsync(data.Access, cancellationToken);
            return profile;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    internal async Task SaveAsync(PairedAccessState access, CancellationToken cancellationToken)
    {
        var updated = new ProfileData { Secret = _data.Secret, RelayOrigin = _data.RelayOrigin, Access = access };
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, updated, CliJsonContext.Default.ProfileData, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, _path, overwrite: true);
            _data = updated;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public ValueTask DisposeAsync() => _lease.DisposeAsync();
}

internal sealed class ProfileData
{
    public int Version { get; set; } = 1;
    public string Secret { get; set; } = "";
    public string RelayOrigin { get; set; } = "";
    public PairedAccessState Access { get; set; } = new();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ProfileData))]
internal partial class CliJsonContext : JsonSerializerContext;
