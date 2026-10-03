using System.Globalization;
using System.Text.Json;

namespace Tilsynsvakt.Api;

public static class Req
{
    public const int MaxBodyBytes = 2048;

    // Not JsonSerializerDefaults.Web: that would accept numbers sent as strings.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static DateOnly Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw Errors.InvalidDate();

    public static int Id(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw Errors.InvalidId();

    public static async Task<T> BodyAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        if (!request.HasJsonContentType())
        {
            throw Errors.UnsupportedMedia();
        }

        if (request.ContentLength > MaxBodyBytes)
        {
            throw Errors.RequestTooLarge();
        }

        // One byte over the limit is enough to detect oversized chunked bodies.
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > MaxBodyBytes)
        {
            throw Errors.RequestTooLarge();
        }

        try
        {
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, total), JsonOptions) ?? throw Errors.InvalidBody();
        }
        catch (JsonException)
        {
            throw Errors.InvalidBody();
        }
    }
}
