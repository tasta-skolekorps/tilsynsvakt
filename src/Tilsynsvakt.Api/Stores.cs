namespace Tilsynsvakt.Api;

public sealed record SignUpResult(ShiftDto Shift, bool Created);

public interface IStores
{
    Task<IReadOnlyList<GuardDto>> GetActiveGuardsAsync(CancellationToken ct);
    Task<IReadOnlyList<AdminGuardDto>> GetAllGuardsAsync(CancellationToken ct);
    Task<IReadOnlyList<ShiftDto>> GetShiftsAsync(DateOnly from, DateOnly to, ShiftCalendar calendar, CancellationToken ct);
    Task<ShiftDto?> GetShiftAsync(DateOnly date, CancellationToken ct);
    Task<SignUpResult> SignUpAsync(DateOnly date, int guardId, CancellationToken ct);
    Task<ShiftDto> ReplaceAsync(DateOnly date, int guardId, int expectedGuardId, CancellationToken ct);
    Task<ShiftDto> SetSignOffAsync(DateOnly date, int guardId, DateTimeOffset? signedOffAt, CancellationToken ct);
    Task DeleteAsync(DateOnly date, int? expectedGuardId, CancellationToken ct);
    Task<IReadOnlyList<SwapRequestDto>> GetSwapRequestsAsync(CancellationToken ct);
    Task<SwapRequestDto> CreateSwapRequestAsync(DateOnly date, DateOnly targetDate, int requesterId, CancellationToken ct);
    Task AcceptSwapRequestAsync(DateOnly date, DateOnly targetDate, int guardId, CancellationToken ct);
    Task DeleteSwapRequestAsync(DateOnly date, DateOnly targetDate, int guardId, CancellationToken ct);
    Task<AdminGuardDto> CreateGuardAsync(string? name, string? phone, CancellationToken ct);
    Task<AdminGuardDto> UpdateGuardAsync(int id, string? name, string? phone, bool? active, CancellationToken ct);
    Task DeactivateGuardAsync(int id, CancellationToken ct);
}
