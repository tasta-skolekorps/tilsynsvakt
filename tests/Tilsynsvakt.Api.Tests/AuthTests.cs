using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Tilsynsvakt.Api;

namespace Tilsynsvakt.Api.Tests;

public sealed class AuthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Origin = "https://example.github.io";

    public static TheoryData<string, string> ProtectedRequests => new()
    {
        { "GET", "/api/guards" },
        { "GET", "/api/shifts?from=2026-10-01&to=2026-10-31" },
        { "GET", "/api/swap-requests" },
        { "POST", "/api/shifts/2027-01-05/signup" },
    };

    [Fact]
    public async Task Login_with_correct_password_returns_derived_key_without_caching()
    {
        using var client = factory.CreateClientWithoutApiKey();

        using var response = await client.PostAsync("/api/auth/login",
            JsonContent.Create(new { password = ApiFactory.TestAccessPassword }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var apiKey = body.RootElement.GetProperty("apiKey").GetString();
        Assert.Equal(AccessKey.Derive(ApiFactory.TestAccessPassword), apiKey);
        Assert.StartsWith("tv1.", apiKey);
        Assert.DoesNotContain(ApiFactory.TestAccessPassword, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData(" test-access-password")]
    [InlineData("test-access-password ")]
    public async Task Login_with_wrong_password_is_rejected(string password)
    {
        using var client = factory.CreateClientWithoutApiKey();

        using var response = await client.PostAsync("/api/auth/login", JsonContent.Create(new { password }));

        using var problem = await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "invalid_password");
        Assert.Equal("Feil passord", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal("Passordet er feil. Prøv igjen.", problem.RootElement.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"password":""}""")]
    [InlineData("""{"password":"   "}""")]
    [InlineData("""{"password":null}""")]
    [InlineData("{not json")]
    public async Task Login_with_missing_or_empty_password_is_invalid_body(string json)
    {
        using var client = factory.CreateClientWithoutApiKey();

        using var response = await client.PostAsync("/api/auth/login", new StringContent(json, Encoding.UTF8, "application/json"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_body");
    }

    [Fact]
    public async Task Login_with_plain_text_is_unsupported_media_type()
    {
        using var client = factory.CreateClientWithoutApiKey();

        using var response = await client.PostAsync("/api/auth/login",
            new StringContent(ApiFactory.TestAccessPassword, Encoding.UTF8, "text/plain"));

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
    }

    [Theory]
    [MemberData(nameof(ProtectedRequests))]
    public async Task Protected_endpoints_require_a_valid_api_key(string method, string path)
    {
        using var client = factory.CreateClientWithoutApiKey();

        using var missing = await client.SendAsync(Request(method, path));
        using var missingProblem = await AssertProblemAsync(missing, HttpStatusCode.Unauthorized, "access_key_required");
        Assert.Equal("Innlogging kreves", missingProblem.RootElement.GetProperty("title").GetString());
        Assert.Equal("Logg inn med passordet for å se vaktlisten.", missingProblem.RootElement.GetProperty("detail").GetString());

        var wrongRequest = Request(method, path);
        wrongRequest.Headers.Add(AccessKey.HeaderName, AccessKey.Derive("some-other-password"));
        using var wrong = await client.SendAsync(wrongRequest);
        await AssertProblemAsync(wrong, HttpStatusCode.Unauthorized, "access_key_required");

        var emptyRequest = Request(method, path);
        emptyRequest.Headers.TryAddWithoutValidation(AccessKey.HeaderName, "");
        using var empty = await client.SendAsync(emptyRequest);
        await AssertProblemAsync(empty, HttpStatusCode.Unauthorized, "access_key_required");

        var correctRequest = Request(method, path);
        correctRequest.Headers.Add(AccessKey.HeaderName, ApiFactory.TestApiKey);
        using var correct = await client.SendAsync(correctRequest);
        Assert.NotEqual(HttpStatusCode.Unauthorized, correct.StatusCode);
    }

    [Fact]
    public async Task Unknown_api_paths_are_gated_before_routing()
    {
        using var client = factory.CreateClientWithoutApiKey();

        using var response = await client.GetAsync("/api/does-not-exist");

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "access_key_required");
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    [InlineData("/openapi/v1.json")]
    public async Task Operational_endpoints_stay_open_without_api_key(string path)
    {
        using var client = factory.CreateClientWithoutApiKey();

        using var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Openapi_document_includes_the_login_endpoint()
    {
        using var client = factory.CreateClientWithoutApiKey();

        var document = await client.GetStringAsync("/openapi/v1.json");

        Assert.Contains("/api/auth/login", document);
    }

    [Fact]
    public async Task Admin_endpoints_use_admin_credentials_without_api_key()
    {
        using var client = factory.CreateClientWithoutApiKey();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/guards");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ApiFactory.TestAdminUsername}:{ApiFactory.TestAdminPassword}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", encoded);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Admin_endpoints_without_admin_credentials_still_return_admin_unauthorized()
    {
        using var client = factory.CreateClientWithoutApiKey();

        using var response = await client.GetAsync("/api/admin/guards");

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Login_is_rate_limited_per_window()
    {
        using var limited = new ApiFactory(new Dictionary<string, string?> { ["RateLimit:LoginPerMinute"] = "2" });
        using var client = limited.CreateClientWithoutApiKey();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var allowed = await client.PostAsync("/api/auth/login", JsonContent.Create(new { password = "wrong-password" }));
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        using var rejected = await client.PostAsync("/api/auth/login",
            JsonContent.Create(new { password = ApiFactory.TestAccessPassword }));
        await AssertProblemAsync(rejected, HttpStatusCode.TooManyRequests, "rate_limited");
    }

    [Fact]
    public async Task Cors_preflight_allows_the_api_key_header_from_the_frontend_origin()
    {
        using var client = factory.CreateClientWithoutApiKey();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/guards");
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "x-api-key");

        using var response = await client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, $"Preflight returned {(int)response.StatusCode}");
        Assert.Equal(Origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var allowedHeaders = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("x-api-key", allowedHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cors_preflight_for_login_is_allowed()
    {
        using var client = factory.CreateClientWithoutApiKey();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/auth/login");
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        using var response = await client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode, $"Preflight returned {(int)response.StatusCode}");
        Assert.Equal(Origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Gate_rejection_carries_cors_headers_for_the_frontend_origin()
    {
        using var client = factory.CreateClientWithoutApiKey();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/guards");
        request.Headers.Add("Origin", Origin);

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "access_key_required");
        Assert.Equal(Origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Startup_fails_closed_without_access_password(string password)
    {
        using var unconfigured = new ApiFactory(new Dictionary<string, string?> { ["Access:Password"] = password });

        var error = Assert.ThrowsAny<Exception>(() => unconfigured.CreateClient());

        Assert.Contains(Flatten(error), e => e is InvalidOperationException && e.Message == "Access:Password must be configured.");
    }

    [Fact]
    public void Derive_is_deterministic_and_password_specific()
    {
        var first = AccessKey.Derive("password-one");

        Assert.Equal(first, AccessKey.Derive("password-one"));
        Assert.NotEqual(first, AccessKey.Derive("password-two"));
        Assert.StartsWith("tv1.", first);
        Assert.DoesNotContain("password-one", first);
        Assert.DoesNotContain("=", first);
        Assert.DoesNotContain("+", first);
        Assert.DoesNotContain("/", first);
        Assert.Equal(47, first.Length);
    }

    [Fact]
    public void Derive_matches_the_documented_formula()
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes("password-one"));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes("tilsynsvakt-api-key-v1"));
        var expected = "tv1." + Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(expected, AccessKey.Derive("password-one"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("tv1.wrong", false)]
    [InlineData("expected-key", true)]
    public void Matches_compares_supplied_key(string? supplied, bool expected) =>
        Assert.Equal(expected, AccessKey.Matches("expected-key", supplied));

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { guardId = 1 });
        }

        return request;
    }

    private static IEnumerable<Exception> Flatten(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                {
                    yield return inner;
                }
            }
        }
    }

    private static async Task<JsonDocument> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.Equal((int)status, document.RootElement.GetProperty("status").GetInt32());
        return document;
    }
}
