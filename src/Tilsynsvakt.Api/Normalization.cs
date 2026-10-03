using System.Globalization;
using System.Text;

namespace Tilsynsvakt.Api;

public static class Normalization
{
    public static (string Name, string Key) Name(string? raw)
    {
        // string.Normalize throws on unpaired surrogates, so reject them first.
        if (raw is null || !IsWellFormed(raw))
        {
            throw Errors.InvalidName();
        }

        var name = CollapseSpaces(raw.Trim().Normalize(NormalizationForm.FormC));
        var runes = name.EnumerateRunes().ToArray();

        if (runes.Length is < 2 or > 80 || !Rune.IsLetter(runes[0]))
        {
            throw Errors.InvalidName();
        }

        foreach (var rune in runes.Skip(1))
        {
            if (!(Rune.IsLetter(rune) || IsCombiningMark(rune) || IsAllowedPunctuation(rune)))
            {
                throw Errors.InvalidName();
            }
        }

        return (name, name.ToUpperInvariant().Normalize(NormalizationForm.FormC));
    }

    public static string Phone(string? raw)
    {
        if (raw is null)
        {
            throw Errors.InvalidPhone();
        }

        var compact = raw.Trim(' ');
        if (compact.Length == 0 || !compact.All(c => char.IsAsciiDigit(c) || c is ' ' or '-' or '+'))
        {
            throw Errors.InvalidPhone();
        }

        compact = compact.Replace(" ", "").Replace("-", "");
        if (compact.Contains('+') && !compact.StartsWith("+47", StringComparison.Ordinal))
        {
            throw Errors.InvalidPhone();
        }

        var number = compact.StartsWith("+47", StringComparison.Ordinal) ? compact[3..]
            : compact.StartsWith("0047", StringComparison.Ordinal) ? compact[4..]
            : compact;

        if (number.Length != 8 || !number.All(char.IsAsciiDigit) || number[0] < '2')
        {
            throw Errors.InvalidPhone();
        }

        return "+47" + number;
    }

    private static bool IsWellFormed(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string CollapseSpaces(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == ' ' && builder.Length > 0 && builder[^1] == ' ')
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsCombiningMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    private static bool IsAllowedPunctuation(Rune rune) => rune.IsBmp && " '.-".Contains((char)rune.Value);
}
