using System.Text.Json;

namespace NxInstaller.Core.Pif;

/// <summary>
/// Google's published attestation status list. Entries are keyed by certificate serial number;
/// a present entry means Google no longer trusts that certificate. Fetched fresh (small JSON)
/// and cached briefly so repeated checks don't re-download it.
/// </summary>
public sealed class RevocationList
{
    public const string Url = "https://android.googleapis.com/attestation/status";

    readonly IReadOnlyDictionary<string, string> _status; // serial -> status (REVOKED / SUSPENDED)
    public DateTimeOffset FetchedAt { get; }

    RevocationList(IReadOnlyDictionary<string, string> status, DateTimeOffset at) { _status = status; FetchedAt = at; }

    /// <summary>Serials are compared lowercase with leading zeros removed, matching how Google lists them.</summary>
    public static string Normalize(string serial) => serial.Trim().ToLowerInvariant().TrimStart('0') is { Length: > 0 } s ? s : "0";

    public string? StatusOf(string serial) => _status.GetValueOrDefault(Normalize(serial));
    public bool IsListed(string serial) => _status.ContainsKey(Normalize(serial));

    public static async Task<RevocationList> FetchAsync(HttpClient http, CancellationToken ct)
    {
        // Cache-buster: the endpoint is served through a CDN that otherwise returns a stale copy.
        var json = await http.GetStringAsync($"{Url}?ts={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", ct);
        return Parse(json);
    }

    public static RevocationList Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("entries", out var entries))
            foreach (var e in entries.EnumerateObject())
            {
                var status = e.Value.TryGetProperty("status", out var s) ? s.GetString() ?? "REVOKED" : "REVOKED";
                map[Normalize(e.Name)] = status;
            }
        return new RevocationList(map, DateTimeOffset.UtcNow);
    }
}
