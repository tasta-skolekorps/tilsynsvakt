using System.Net;
using System.Text;
using System.Text.Json;
using Tilsynsvakt.SpondSync;

namespace Tilsynsvakt.SpondSync.Tests;

public sealed class BackendClientTests
{
    private const string Password = "test-placeholder-password";
    private static readonly Uri BaseUrl = new("https://api.example.invalid/");

    [Fact]
    public void ApiKeyHeader_is_X_Api_Key() => Assert.Equal("X-Api-Key", BackendClient.ApiKeyHeader);

    [Fact]
    public async Task LoginAsync_posts_json_password_and_returns_api_key()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"apiKey":"tv1.test-key"}""");
        using var http = new HttpClient(handler);

        var key = await BackendClient.LoginAsync(http, BaseUrl, Password, CancellationToken.None);

        Assert.Equal("tv1.test-key", key);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://api.example.invalid/api/auth/login"), request.Uri);
        Assert.Equal("application/json", request.ContentType);
        Assert.False(request.HasApiKeyHeader);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(Password, body.RootElement.GetProperty("password").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"code":"invalid_password"}""", "Vakt-API-et avviste API_PASSWORD.")]
    [InlineData((HttpStatusCode)429, """{"code":"rate_limited"}""", "Vakt-API-et begrenser innlogging (429). Prøv igjen senere.")]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "Innlogging mot vakt-API-et mislyktes (HTTP 500).")]
    [InlineData(HttpStatusCode.OK, "{}", "Innlogging mot vakt-API-et mislyktes (HTTP 200).")]
    [InlineData(HttpStatusCode.OK, """{"apiKey":""}""", "Innlogging mot vakt-API-et mislyktes (HTTP 200).")]
    public async Task LoginAsync_failures_raise_Norwegian_SyncException_without_secrets(HttpStatusCode status, string body, string expected)
    {
        using var http = new HttpClient(new FakeHandler(status, body));

        var error = await Assert.ThrowsAsync<SyncException>(() =>
            BackendClient.LoginAsync(http, BaseUrl, Password, CancellationToken.None));

        Assert.Equal(expected, error.Message);
        Assert.DoesNotContain(Password, error.Message);
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? ContentType, bool HasApiKeyHeader, string Body);

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.Contains("X-Api-Key"),
                content));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
