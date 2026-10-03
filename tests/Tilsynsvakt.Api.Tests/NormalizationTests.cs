using Tilsynsvakt.Api;

namespace Tilsynsvakt.Api.Tests;

public sealed class NormalizationTests
{
    [Theory]
    [InlineData("400 00 000", "+4740000000")]
    [InlineData("+47 400 00 000", "+4740000000")]
    [InlineData("0047-40000000", "+4740000000")]
    public void Phone_accepts_supported_norwegian_formats(string raw, string expected) =>
        Assert.Equal(expected, Normalization.Phone(raw));

    [Theory]
    [InlineData("100 00 000")]
    [InlineData("400 00 00")]
    [InlineData("+46 40000000")]
    [InlineData("400 00 00x")]
    public void Phone_rejects_invalid_values(string raw)
    {
        var error = Assert.Throws<ApiException>(() => Normalization.Phone(raw));
        Assert.Equal("invalid_phone", error.Code);
    }

    [Fact]
    public void Name_trims_collapses_spaces_and_normalizes_to_NFC()
    {
        var (name, _) = Normalization.Name("  A\u030A   v a  ");

        Assert.Equal("Å v a", name);
    }

    [Fact]
    public void Name_keys_match_after_case_and_whitespace_normalization()
    {
        var first = Normalization.Name("  anna   larsen ");
        var second = Normalization.Name("ANNA LARSEN");

        Assert.Equal(first.Key, second.Key);
        Assert.Equal("anna larsen", first.Name);
        Assert.Equal("ANNA LARSEN", first.Key);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("1Anna")]
    public void Name_rejects_short_or_non_letter_initial(string raw)
    {
        var error = Assert.Throws<ApiException>(() => Normalization.Name(raw));
        Assert.Equal("invalid_name", error.Code);
    }

    [Fact]
    public void Name_rejects_an_unpaired_surrogate()
    {
        var raw = new string(new[] { '\ud800', 'x' });

        var error = Assert.Throws<ApiException>(() => Normalization.Name(raw));
        Assert.Equal("invalid_name", error.Code);
    }
}