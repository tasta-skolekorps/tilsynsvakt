using System.Security.Cryptography;
using System.Text;

namespace Tilsynsvakt.Api;

public static class AccessKey
{
    public const string HeaderName = "X-Api-Key";
    private const string Prefix = "tv1.";
    private const string DerivationLabel = "tilsynsvakt-api-key-v1";

    public static string Derive(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(DerivationLabel));
        return Prefix + Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool Matches(string expected, string? supplied)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        // Hashing first gives equal-length inputs, so the comparison time does not reveal length.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    }
}

public sealed class AccessSettings(string password)
{
    public string Password { get; } = password;
    public string ApiKey { get; } = AccessKey.Derive(password);
}

public sealed class AccessKeyMiddleware(RequestDelegate next, AccessSettings settings)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments("/api")
            || HttpMethods.IsOptions(request.Method)
            || request.Path.StartsWithSegments("/api/auth/login")
            || request.Path.StartsWithSegments("/api/admin"))
        {
            await next(context);
            return;
        }

        var supplied = request.Headers[AccessKey.HeaderName];
        if (supplied.Count != 1 || !AccessKey.Matches(settings.ApiKey, supplied[0]))
        {
            await Errors.AccessKeyRequired().ToResult().ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
