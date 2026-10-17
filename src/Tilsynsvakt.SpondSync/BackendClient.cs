using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Tilsynsvakt.SpondSync;

public static class BackendClient
{
    public const string ApiKeyHeader = "X-Api-Key";

    public static async Task<string> LoginAsync(HttpClient http, Uri apiBaseUrl, string password, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(new Uri(apiBaseUrl, "api/auth/login"),
            new { password }, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new SyncException("Vakt-API-et avviste API_PASSWORD.");
            case HttpStatusCode.TooManyRequests:
                throw new SyncException("Vakt-API-et begrenser innlogging (429). Prøv igjen senere.");
        }

        var failure = new SyncException($"Innlogging mot vakt-API-et mislyktes (HTTP {(int)response.StatusCode}).");
        if (!response.IsSuccessStatusCode) throw failure;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("apiKey", out var key)
                && key.ValueKind == JsonValueKind.String
                && key.GetString() is { Length: > 0 } value
                    ? value
                    : throw failure;
        }
        catch (JsonException)
        {
            throw failure;
        }
    }
}
