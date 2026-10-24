using System.Text.Json;

namespace Kiosk.Client;

internal sealed record Bookmark
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
}

internal sealed record ApplicationEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";

    /// <summary>Path of the built-in Kiosk notepad; listed like any other application so it can be removed.</summary>
    internal const string BuiltInNotes = "kiosk:notatnik";
    internal static ApplicationEntry Notes => new() { Name = "Notatnik", Path = BuiltInNotes };
    internal bool IsBuiltInNotes => string.Equals(Path, BuiltInNotes, StringComparison.OrdinalIgnoreCase);
}

internal static class Configuration
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    internal static Config Load(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Konfiguracja jest zbyt duża (maksymalnie 4 MB).");
        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), ReadOptions) ?? throw new InvalidDataException("Konfiguracja jest pusta.");
        config = config.Migrate(); config.Validate(); return config;
    }
    internal static bool IsWebUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    internal static bool IsRdpServer(string? host) =>
        host is { Length: > 0 and <= 255 } && !host.Any(char.IsWhiteSpace) &&
        Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;

    internal static bool IsEntraRdpServer(string host) =>
        Uri.CheckHostName(host) == UriHostNameType.Dns && host.Contains('.') && !host.EndsWith('.');

    internal static void Save(string path, Config config)
    {
        config.Validate();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(config, WriteOptions));
            // ReplaceFile preserves the destination ACL; a rename-over could
            // unintentionally make an administrator-managed config writable.
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
