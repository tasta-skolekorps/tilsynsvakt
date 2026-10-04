using System.Globalization;

namespace Tilsynsvakt.Api;

/// <summary>Public guard information returned by the API.</summary>
public sealed record GuardDto(int Id, string Name, string Phone);

/// <summary>Guard information returned by administrative endpoints.</summary>
public sealed record AdminGuardDto(int Id, string Name, string Phone, bool Active);

/// <summary>A guard's signup state for one shift date.</summary>
public sealed record ShiftDto(string Date, string DayOfWeek, string Status, GuardDto? Guard)
{
    public static ShiftDto From(DateOnly date, GuardDto? guard) =>
        new(Iso(date), date.DayOfWeek.ToString().ToLowerInvariant(), guard is null ? "open" : "taken", guard);

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>A range of eligible shifts returned by the API.</summary>
public sealed record ShiftListDto(string From, string To, IReadOnlyList<ShiftDto> Shifts);

/// <summary>A pending request to swap two taken shifts, awaiting approval by the target guard.</summary>
public sealed record SwapRequestDto(string Date, string TargetDate, GuardDto Requester, GuardDto Target);

/// <summary>Request body for creating a swap request.</summary>
public sealed record SwapRequestBody(string? Date, string? TargetDate, int? RequesterGuardId);

/// <summary>Request body for accepting a swap request.</summary>
public sealed record SwapAcceptBody(int? GuardId);

/// <summary>Request body for signing up for a shift.</summary>
public sealed record SignUpBody(int? GuardId);

/// <summary>Request body for replacing a shift signup.</summary>
public sealed record ReplaceBody(int? GuardId, int? ExpectedGuardId);

/// <summary>Request body for creating an administrative guard entry.</summary>
public sealed record GuardBody(string? Name, string? Phone);

/// <summary>Request body for updating an administrative guard entry.</summary>
public sealed record AdminGuardBody(string? Name, string? Phone, bool? Active);
