using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Tilsynsvakt.Api;

namespace Tilsynsvakt.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string TestAdminKey = "test-only-admin-key";

    public ApiFactory()
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        Stores = new InMemoryStores();
    }

    public FakeTimeProvider Time { get; }
    public InMemoryStores Stores { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Admin:ApiKey", TestAdminKey);
        builder.UseSetting("Frontend:Origin", "https://example.github.io");
        builder.UseSetting("RateLimit:MutationsPerMinute", "10000");
        builder.UseSetting("RateLimit:AdminPerMinute", "10000");

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Admin:ApiKey"] = TestAdminKey,
                ["Frontend:Origin"] = "https://example.github.io",
                ["RateLimit:MutationsPerMinute"] = "10000",
                ["RateLimit:AdminPerMinute"] = "10000",
            }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.RemoveAll<IStores>();
            services.RemoveAll<IStoreLifecycle>();
            services.AddSingleton<IStores>(Stores);
            services.AddSingleton<IStoreLifecycle>(Stores);
        });
    }
}

public sealed class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private static readonly DateOnly[] AvailableTestDates = Enumerable
        .Range(0, DateOnly.Parse("2027-05-29").DayNumber - DateOnly.Parse("2027-01-05").DayNumber + 1)
        .Select(offset => DateOnly.FromDayNumber(DateOnly.Parse("2027-01-05").DayNumber + offset))
        .Where(date => date.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday)
        .ToArray();
    private static int _dateIndex;

    [Fact]
    public async Task Health_and_liveness_endpoints_are_available()
    {
        using var health = await _client.GetAsync("/health");
        using var alive = await _client.GetAsync("/alive");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
    }

    [Fact]
    public async Task Readiness_failure_does_not_affect_liveness()
    {
        factory.Stores.Ready = false;
        try
        {
            using var health = await _client.GetAsync("/health");
            using var alive = await _client.GetAsync("/alive");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
            Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        }
        finally
        {
            factory.Stores.Ready = true;
        }
    }

    [Fact]
    public async Task Admin_endpoints_require_the_configured_key_and_allow_creation()
    {
        using var noKey = await _client.GetAsync("/api/admin/guards");
        await AssertProblemAsync(noKey, HttpStatusCode.Unauthorized, "unauthorized");

        using var wrongKeyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/admin/guards");
        wrongKeyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-test-key");
        using var wrongKey = await _client.SendAsync(wrongKeyRequest);
        await AssertProblemAsync(wrongKey, HttpStatusCode.Unauthorized, "unauthorized");

        var created = await CreateGuardAsync();
        Assert.True(created.Id > 0);
    }

    [Fact]
    public async Task Guard_list_contains_only_active_guards()
    {
        var active = await CreateGuardAsync();
        var inactive = await CreateGuardAsync();
        await DeactivateGuardAsync(inactive.Id);

        using var response = await _client.GetAsync("/api/guards");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var ids = document.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ToArray();
        Assert.Contains(active.Id, ids);
        Assert.DoesNotContain(inactive.Id, ids);
    }

    [Fact]
    public async Task October_range_has_thirteen_open_tuesday_through_thursday_shifts()
    {
        using var response = await _client.GetAsync("/api/shifts?from=2026-10-01&to=2026-10-31");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var shifts = document.RootElement.GetProperty("shifts").EnumerateArray().ToArray();

        Assert.Equal(13, shifts.Length);
        Assert.All(shifts, shift =>
        {
            Assert.Contains(shift.GetProperty("dayOfWeek").GetString(), new[] { "tuesday", "wednesday", "thursday" });
            Assert.Equal("open", shift.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, shift.GetProperty("guard").ValueKind);
        });
    }

    [Fact]
    public async Task Calendar_period_boundaries_and_single_shift_lookup_follow_the_contract()
    {
        using var november = await _client.GetAsync("/api/shifts/2026-11-26");
        Assert.Equal(HttpStatusCode.OK, november.StatusCode);

        using var december = await _client.GetAsync("/api/shifts/2026-12-01");
        await AssertProblemAsync(december, HttpStatusCode.NotFound, "not_a_shift_day");

        using var january = await _client.GetAsync("/api/shifts/2027-01-05");
        Assert.Equal(HttpStatusCode.OK, january.StatusCode);
    }

    [Theory]
    [InlineData("not-a-date", "2026-10-31", "invalid_date")]
    [InlineData("2026-10-31", "2026-10-01", "invalid_range")]
    [InlineData("2026-10-01", "2027-10-02", "invalid_range")]
    public async Task Invalid_shift_ranges_return_problem_details(string from, string to, string code)
    {
        using var response = await _client.GetAsync($"/api/shifts?from={from}&to={to}");
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, code);
    }

    [Fact]
    public async Task Signup_is_idempotent_and_conflicts_include_the_current_shift()
    {
        var firstGuard = await CreateGuardAsync();
        var otherGuard = await CreateGuardAsync();
        var date = NextTestDate();

        using var first = await PostSignupAsync(date, firstGuard.Id);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.NotNull(first.Headers.Location);
        Assert.Contains("/api/shifts/", first.Headers.Location!.ToString());

        using var repeat = await PostSignupAsync(date, firstGuard.Id);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);

        using var conflict = await PostSignupAsync(date, otherGuard.Id);
        using var problem = await AssertProblemAsync(conflict, HttpStatusCode.Conflict, "shift_taken");
        var current = problem.RootElement.GetProperty("currentShift");
        Assert.Equal("taken", current.GetProperty("status").GetString());
        Assert.Equal(firstGuard.Id, current.GetProperty("guard").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Signup_rejects_non_shift_past_and_too_far_dates_and_unknown_or_inactive_guards()
    {
        var guard = await CreateGuardAsync();

        using var monday = await PostSignupAsync("2026-10-05", guard.Id);
        await AssertProblemAsync(monday, HttpStatusCode.UnprocessableEntity, "not_a_shift_day");

        using var past = await PostSignupAsync("2026-09-29", guard.Id);
        await AssertProblemAsync(past, HttpStatusCode.UnprocessableEntity, "date_in_past");

        using var distant = await PostSignupAsync("2027-11-09", guard.Id);
        await AssertProblemAsync(distant, HttpStatusCode.UnprocessableEntity, "date_too_far_ahead");

        using var unknown = await PostSignupAsync(NextTestDate(), 999999);
        await AssertProblemAsync(unknown, HttpStatusCode.UnprocessableEntity, "unknown_guard");

        await DeactivateGuardAsync(guard.Id);
        using var inactive = await PostSignupAsync(NextTestDate(), guard.Id);
        await AssertProblemAsync(inactive, HttpStatusCode.UnprocessableEntity, "unknown_guard");
    }

    [Fact]
    public async Task Signup_validates_dates_content_type_json_and_body_size()
    {
        var guard = await CreateGuardAsync();
        using var invalidDate = await PostSignupAsync("2026-10-6", guard.Id);
        await AssertProblemAsync(invalidDate, HttpStatusCode.BadRequest, "invalid_date");

        foreach (var value in new[] { "{\"guardId\":\"12\"}", "{\"guardId\":12.5}", "{\"guardId\":null}", "{}" })
        {
            using var invalidBody = await PostRawAsync($"/api/shifts/{NextTestDate():yyyy-MM-dd}/signup", value, "application/json");
            await AssertProblemAsync(invalidBody, HttpStatusCode.BadRequest, "invalid_body");
        }

        using var wrongType = await PostRawAsync($"/api/shifts/{NextTestDate():yyyy-MM-dd}/signup", "{}", "text/plain");
        await AssertProblemAsync(wrongType, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");

        using var malformed = await PostRawAsync($"/api/shifts/{NextTestDate():yyyy-MM-dd}/signup", "{", "application/json");
        await AssertProblemAsync(malformed, HttpStatusCode.BadRequest, "invalid_body");

        using var oversized = await PostRawAsync($"/api/shifts/{NextTestDate():yyyy-MM-dd}/signup", new string('x', 2049), "application/json");
        await AssertProblemAsync(oversized, HttpStatusCode.RequestEntityTooLarge, "request_too_large");
    }

    [Fact]
    public async Task Replace_checks_occupant_and_active_target()
    {
        var current = await CreateGuardAsync();
        var replacement = await CreateGuardAsync();
        var inactive = await CreateGuardAsync();
        var date = NextTestDate();
        using var signup = await PostSignupAsync(date, current.Id);
        Assert.Equal(HttpStatusCode.Created, signup.StatusCode);

        using var stale = await PutReplaceAsync(date, replacement.Id, replacement.Id);
        using var staleProblem = await AssertProblemAsync(stale, HttpStatusCode.Conflict, "shift_changed");
        Assert.Equal(current.Id, staleProblem.RootElement.GetProperty("currentShift").GetProperty("guard").GetProperty("id").GetInt32());

        using var unchanged = await PutReplaceAsync(date, current.Id, current.Id);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);

        using var changed = await PutReplaceAsync(date, replacement.Id, current.Id);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        await DeactivateGuardAsync(inactive.Id);
        using var inactiveTarget = await PutReplaceAsync(date, inactive.Id, replacement.Id);
        await AssertProblemAsync(inactiveTarget, HttpStatusCode.UnprocessableEntity, "unknown_guard");

        using var open = await PutReplaceAsync(NextTestDate(), current.Id, current.Id);
        await AssertProblemAsync(open, HttpStatusCode.NotFound, "shift_not_taken");
    }

    [Fact]
    public async Task Delete_requires_matching_precondition_and_is_idempotent_when_open()
    {
        var guard = await CreateGuardAsync();
        var other = await CreateGuardAsync();
        var date = NextTestDate();
        using var signup = await PostSignupAsync(date, guard.Id);
        Assert.Equal(HttpStatusCode.Created, signup.StatusCode);

        using var missing = await _client.DeleteAsync($"/api/shifts/{date:yyyy-MM-dd}");
        await AssertProblemAsync(missing, (HttpStatusCode)428, "precondition_required");

        using var stale = await _client.DeleteAsync($"/api/shifts/{date:yyyy-MM-dd}?expectedGuardId={other.Id}");
        using var staleProblem = await AssertProblemAsync(stale, HttpStatusCode.Conflict, "shift_changed");
        Assert.Equal(guard.Id, staleProblem.RootElement.GetProperty("currentShift").GetProperty("guard").GetProperty("id").GetInt32());

        using var invalidId = await _client.DeleteAsync($"/api/shifts/{date:yyyy-MM-dd}?expectedGuardId=not-an-id");
        await AssertProblemAsync(invalidId, HttpStatusCode.BadRequest, "invalid_id");

        using var deleted = await _client.DeleteAsync($"/api/shifts/{date:yyyy-MM-dd}?expectedGuardId={guard.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var repeated = await _client.DeleteAsync($"/api/shifts/{date:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
    }

    [Fact]
    public async Task Parallel_signups_allow_exactly_one_guard_to_claim_a_date()
    {
        var guards = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => CreateGuardAsync()));
        var date = NextTestDate();
        var responses = await Task.WhenAll(guards.Select(guard => PostSignupAsync(date, guard.Id)));

        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Equal(4, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            foreach (var response in responses.Where(response => response.StatusCode == HttpStatusCode.Conflict))
            {
                await AssertProblemAsync(response, HttpStatusCode.Conflict, "shift_taken");
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Deactivation_preserves_signup_but_allows_another_caller_to_cancel()
    {
        var formerGuard = await CreateGuardAsync();
        var other = await CreateGuardAsync();
        var date = NextTestDate();
        using var signup = await PostSignupAsync(date, formerGuard.Id);
        Assert.Equal(HttpStatusCode.Created, signup.StatusCode);

        await DeactivateGuardAsync(formerGuard.Id);
        using var existing = await _client.GetAsync($"/api/shifts/{date:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        using (var existingDocument = await JsonDocument.ParseAsync(await existing.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(formerGuard.Id, existingDocument.RootElement.GetProperty("guard").GetProperty("id").GetInt32());
        }

        using var signupAgain = await PostSignupAsync(NextTestDate(), formerGuard.Id);
        await AssertProblemAsync(signupAgain, HttpStatusCode.UnprocessableEntity, "unknown_guard");

        using var cancelled = await _client.DeleteAsync($"/api/shifts/{date:yyyy-MM-dd}?expectedGuardId={formerGuard.Id}");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.True(other.Id > 0);
    }

    [Fact]
    public async Task Signup_uses_Oslo_local_date_at_the_midnight_boundary()
    {
        using var boundaryFactory = new ApiFactory();
        using var boundaryClient = boundaryFactory.CreateClient();
        var guard = await CreateGuardAsync(boundaryClient);
        boundaryFactory.Time.SetUtcNow(new DateTimeOffset(2026, 10, 6, 22, 30, 0, TimeSpan.Zero));

        using var response = await boundaryClient.PostAsync(
            "/api/shifts/2026-10-06/signup", JsonContent.Create(new { guardId = guard.Id }));
        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "date_in_past");
    }

    [Fact]
    public async Task Cors_echoes_only_the_configured_origin()
    {
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("https://example.github.io", configuration["Frontend:Origin"]);
        Assert.Equal(10000, configuration.GetValue<int>("RateLimit:AdminPerMinute"));

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/guards");
        allowedRequest.Headers.Add("Origin", "https://example.github.io");
        using var allowed = await _client.SendAsync(allowedRequest);
        Assert.Equal("https://example.github.io", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/guards");
        deniedRequest.Headers.Add("Origin", "https://not-allowed.example");
        using var denied = await _client.SendAsync(deniedRequest);
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Problem_responses_are_Norwegian_RFC9457_without_stack_traces()
    {
        using var response = await _client.GetAsync("/api/shifts/not-a-date");
        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_date");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Ugyldig dato", problem.RootElement.GetProperty("title").GetString());
        Assert.Contains("Bruk datoformatet", problem.RootElement.GetProperty("detail").GetString());
        var body = problem.RootElement.GetRawText();
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at Tilsynsvakt.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Swap_request_must_be_approved_by_the_target_guard_and_swaps_both_shifts()
    {
        var requester = await CreateGuardAsync();
        var target = await CreateGuardAsync();
        var date = NextTestDate();
        var targetDate = NextTestDate();
        (await PostSignupAsync(date, requester.Id)).Dispose();
        (await PostSignupAsync(targetDate, target.Id)).Dispose();
        var path = $"/api/swap-requests/{date:yyyy-MM-dd}/{targetDate:yyyy-MM-dd}";

        using var created = await _client.PostAsync("/api/swap-requests", JsonContent.Create(new
        {
            date = date.ToString("yyyy-MM-dd"),
            targetDate = targetDate.ToString("yyyy-MM-dd"),
            requesterGuardId = requester.Id,
        }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using var byRequester = await _client.PostAsync($"{path}/accept", JsonContent.Create(new { guardId = requester.Id }));
        await AssertProblemAsync(byRequester, HttpStatusCode.Forbidden, "swap_forbidden");

        using var accepted = await _client.PostAsync($"{path}/accept", JsonContent.Create(new { guardId = target.Id }));
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);

        using var first = await _client.GetAsync($"/api/shifts/{date:yyyy-MM-dd}");
        using var firstDoc = await JsonDocument.ParseAsync(await first.Content.ReadAsStreamAsync());
        Assert.Equal(target.Id, firstDoc.RootElement.GetProperty("guard").GetProperty("id").GetInt32());
        using var second = await _client.GetAsync($"/api/shifts/{targetDate:yyyy-MM-dd}");
        using var secondDoc = await JsonDocument.ParseAsync(await second.Content.ReadAsStreamAsync());
        Assert.Equal(requester.Id, secondDoc.RootElement.GetProperty("guard").GetProperty("id").GetInt32());

        using var gone = await _client.PostAsync($"{path}/accept", JsonContent.Create(new { guardId = target.Id }));
        await AssertProblemAsync(gone, HttpStatusCode.NotFound, "swap_request_not_found");
    }

    [Fact]
    public async Task Swap_request_can_be_declined_and_is_hidden_when_a_shift_changes()
    {
        var requester = await CreateGuardAsync();
        var target = await CreateGuardAsync();
        var date = NextTestDate();
        var targetDate = NextTestDate();
        (await PostSignupAsync(date, requester.Id)).Dispose();
        (await PostSignupAsync(targetDate, target.Id)).Dispose();
        var key = $"{date:yyyy-MM-dd}/{targetDate:yyyy-MM-dd}";
        var body = JsonContent.Create(new { date = $"{date:yyyy-MM-dd}", targetDate = $"{targetDate:yyyy-MM-dd}", requesterGuardId = requester.Id });
        (await _client.PostAsync("/api/swap-requests", body)).Dispose();

        var listed = await _client.GetStringAsync("/api/swap-requests");
        Assert.Contains(key.Split('/')[0], listed);

        using var decline = await _client.DeleteAsync($"/api/swap-requests/{key}?guardId={target.Id}");
        Assert.Equal(HttpStatusCode.NoContent, decline.StatusCode);
        Assert.DoesNotContain(requester.Name, await _client.GetStringAsync("/api/swap-requests"));

        (await _client.PostAsync("/api/swap-requests", JsonContent.Create(new { date = $"{date:yyyy-MM-dd}", targetDate = $"{targetDate:yyyy-MM-dd}", requesterGuardId = requester.Id }))).Dispose();
        (await _client.DeleteAsync($"/api/shifts/{date:yyyy-MM-dd}?expectedGuardId={requester.Id}")).Dispose();
        Assert.DoesNotContain(requester.Name, await _client.GetStringAsync("/api/swap-requests"));
    }
    private static DateOnly NextTestDate() => AvailableTestDates[Interlocked.Increment(ref _dateIndex) % AvailableTestDates.Length];

    private async Task<(int Id, string Name)> CreateGuardAsync() => await CreateGuardAsync(_client);

    private static async Task<(int Id, string Name)> CreateGuardAsync(HttpClient client)
    {
        var name = "Guard" + string.Concat(Enumerable.Range(0, 12).Select(_ => (char)('a' + Random.Shared.Next(26))));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/guards")
        {
            Content = JsonContent.Create(new { name, phone = "400 00 000" }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiFactory.TestAdminKey);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return (document.RootElement.GetProperty("id").GetInt32(), name);
    }

    private async Task DeactivateGuardAsync(int guardId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/guards/{guardId}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiFactory.TestAdminKey);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private Task<HttpResponseMessage> PostSignupAsync(DateOnly date, int guardId) => PostSignupAsync(date.ToString("yyyy-MM-dd"), guardId);

    private Task<HttpResponseMessage> PostSignupAsync(string date, int guardId) =>
        _client.PostAsync($"/api/shifts/{date}/signup", JsonContent.Create(new { guardId }));

    private async Task<HttpResponseMessage> PostRawAsync(string path, string body, string contentType)
    {
        using var content = new StringContent(body, Encoding.UTF8, contentType);
        return await _client.PostAsync(path, content);
    }

    private Task<HttpResponseMessage> PutReplaceAsync(DateOnly date, int guardId, int expectedGuardId) =>
        _client.PutAsync($"/api/shifts/{date:yyyy-MM-dd}", JsonContent.Create(new { guardId, expectedGuardId }));

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