using System.Net;
using System.Reflection;
using System.Text.Json;
using Tilsynsvakt.SpondSync;

namespace Tilsynsvakt.SpondSync.Tests;

public sealed class SafeHelpersTests
{
    [Fact]
    public void JsonHelpers_ReadOnlyRequestedFields()
    {
        using var document = JsonDocument.Parse("{\"id\":\"test-parent\",\"name\":\"Familien Test\",\"phoneNumber\":\"+4790000001\",\"absent\":null,\"members\":[{\"id\":\"test-child\"}]}");
        var root = document.RootElement;
        Assert.Equal("test-parent", SpondClient.Text(root, "id"));
        Assert.Null(SpondClient.OptionalText(root, "missing"));
        Assert.Null(SpondClient.OptionalText(root, "absent"));
        Assert.Equal("test-child", SpondClient.Text(Assert.Single(SpondClient.Array(SpondClient.Property(root, "members"))), "id"));
    }

    [Fact]
    public void JsonHelpers_MissingOrMalformedFieldsHaveSanitizedErrors()
    {
        using var document = JsonDocument.Parse("{\"name\":\"Familien Test\",\"id\":\"test-parent\",\"phoneNumber\":\"+4790000001\",\"empty\":\"\"}");
        var root = document.RootElement;
        Assert.Equal("Ufullstendig ekstern respons.", Assert.Throws<SyncException>(() => SpondClient.Property(root, "missing")).Message);
        Assert.Equal("Ufullstendig ekstern respons.", Assert.Throws<SyncException>(() => SpondClient.Text(root, "empty")).Message);
        Assert.Equal("Uventet format i ekstern respons.", Assert.Throws<SyncException>(() => SpondClient.Array(root)).Message);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(500)]
    public async Task ReadAsync_FailedResponseNeverLeaksExternalDetails(int status)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("Familien Test test-parent +4790000001")
        };
        var error = await Assert.ThrowsAsync<SyncException>(() => SpondClient.ReadAsync(response, CancellationToken.None));
        Assert.Equal("Ekstern foresp\u00f8rsel mislyktes; ingen responsdetaljer logges.", error.Message);
    }

    [Fact]
    public async Task ReadAsync_ParsesInMemoryJsonWithoutTransport()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"test-event\"}") };
        using var document = await SpondClient.ReadAsync(response, CancellationToken.None);
        Assert.Equal("test-event", SpondClient.Text(document.RootElement, "id"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("Familien Test", " Familien Test")]
    [InlineData("Familien Test%\r\n::error::", " Familien Test%25%0D%0A::error::")]
    public void PublicName_EscapesWorkflowLineInjection(string? name, string expected) =>
        Assert.Equal(expected, FormatPublicName(name));

    [Fact]
    public void PublicName_OnlyIncludesBackendNameNotGuardianIdentifiersOrPhones()
    {
        var group = new TargetGroup("test-group", "test-subgroup",
            [new ChildMember("test-child", ["test-subgroup"], [new Guardian("test-parent", "+4790000001", "test-parent")])]);
        var date = new DateOnly(2026, 11, 26);
        var action = Assert.Single(SyncPlanner.Plan([new(date, "taken", new Guard(1, "Familien Test", "+4790000001"))],
            [], group, new HashSet<DateOnly> { date }, new(2026, 10, 5)));

        Assert.Equal(" Familien Test", FormatPublicName(action.GuardName));
        foreach (var privateValue in new[] { "test-parent", "test-child", "test-group", "test-subgroup", "+4790000001", "90000001" })
            Assert.DoesNotContain(privateValue, FormatPublicName(action.GuardName));
    }

    private static string FormatPublicName(string? name)
    {
        var program = typeof(SyncPlanner).Assembly.GetType("Program", throwOnError: true)!;
        var formatter = Assert.Single(program.GetMethods(BindingFlags.NonPublic | BindingFlags.Static),
            method => method.Name.Contains("g__PublicName|", StringComparison.Ordinal));
        return Assert.IsType<string>(formatter.Invoke(null, new object?[] { name }));
    }
}