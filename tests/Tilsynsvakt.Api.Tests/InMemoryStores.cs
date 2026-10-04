using Tilsynsvakt.Api;

namespace Tilsynsvakt.Api.Tests;

public sealed class InMemoryStores : IStores, IStoreLifecycle
{
    private sealed record Guard(int Id, string Name, string NameKey, string Phone, bool Active);

    private readonly object _gate = new();
    private readonly Dictionary<int, Guard> _guards = [];
    private readonly Dictionary<string, int> _guardIdsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<DateOnly, ShiftDto> _shifts = [];
    private readonly Dictionary<(DateOnly, DateOnly), SwapRequestDto> _swaps = [];
    private int _nextGuardId;
    private int _ready = 1;

    public bool Ready
    {
        get => Volatile.Read(ref _ready) == 1;
        set => Volatile.Write(ref _ready, value ? 1 : 0);
    }

    public Task InitializeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task CheckReadyAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Ready)
        {
            throw new InvalidOperationException("The in-memory store is not ready.");
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GuardDto>> GetActiveGuardsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<GuardDto> guards = _guards.Values
                .Where(guard => guard.Active)
                .OrderBy(guard => guard.NameKey, StringComparer.Ordinal)
                .Select(ToGuardDto)
                .ToArray();
            return Task.FromResult(guards);
        }
    }

    public Task<IReadOnlyList<AdminGuardDto>> GetAllGuardsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<AdminGuardDto> guards = _guards.Values
                .OrderBy(guard => guard.NameKey, StringComparer.Ordinal)
                .Select(guard => new AdminGuardDto(guard.Id, guard.Name, guard.Phone, guard.Active))
                .ToArray();
            return Task.FromResult(guards);
        }
    }

    public Task<IReadOnlyList<ShiftDto>> GetShiftsAsync(DateOnly from, DateOnly to, ShiftCalendar calendar, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var shifts = new List<ShiftDto>();
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                if (calendar.IsShiftDay(date))
                {
                    shifts.Add(_shifts.GetValueOrDefault(date) ?? ShiftDto.From(date, null));
                }
            }

            return Task.FromResult<IReadOnlyList<ShiftDto>>(shifts);
        }
    }

    public Task<IReadOnlyList<ShiftDto>> GetAdminShiftsAsync(DateOnly from, DateOnly to, ShiftCalendar calendar, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var shifts = new List<ShiftDto>();
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                if (calendar.IsAdminDutyDay(date))
                {
                    shifts.Add(_shifts.GetValueOrDefault(date) ?? ShiftDto.From(date, null));
                }
            }

            return Task.FromResult<IReadOnlyList<ShiftDto>>(shifts);
        }
    }

    public Task<ShiftDto?> GetShiftAsync(DateOnly date, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_shifts.GetValueOrDefault(date));
        }
    }

    public Task<SignUpResult> SignUpAsync(DateOnly date, int guardId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var guard = FindActiveGuard(guardId);
            if (_shifts.TryGetValue(date, out var current))
            {
                if (current.Guard!.Id == guardId)
                {
                    return Task.FromResult(new SignUpResult(current, false));
                }

                throw Errors.ShiftTaken(current);
            }

            var shift = ShiftDto.From(date, ToGuardDto(guard));
            _shifts.Add(date, shift);
            return Task.FromResult(new SignUpResult(shift, true));
        }
    }

    public Task<ShiftDto> ReplaceAsync(DateOnly date, int guardId, int expectedGuardId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var guard = FindActiveGuard(guardId);
            if (!_shifts.TryGetValue(date, out var current))
            {
                throw Errors.ShiftNotTaken();
            }

            if (current.Guard!.Id != expectedGuardId)
            {
                throw Errors.ShiftChanged(current);
            }

            if (current.Guard.Id == guardId)
            {
                return Task.FromResult(current);
            }

            var replacement = ShiftDto.From(date, ToGuardDto(guard));
            _shifts[date] = replacement;
            return Task.FromResult(replacement);
        }
    }

    public Task<ShiftDto> SetSignOffAsync(DateOnly date, int guardId, DateTimeOffset? signedOffAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_shifts.TryGetValue(date, out var current))
            {
                throw Errors.ShiftNotTaken();
            }

            if (current.Guard!.Id != guardId)
            {
                throw Errors.ShiftChanged(current);
            }

            var updated = current with { SignedOffAt = signedOffAt?.ToUniversalTime().ToString("O") };
            _shifts[date] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<ShiftDto> UpsertAdminShiftAsync(DateOnly date, int? guardId, DateTimeOffset? signedOffAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (guardId is null)
            {
                _shifts.Remove(date);
                return Task.FromResult(ShiftDto.From(date, null));
            }

            var guard = FindActiveGuard(guardId.Value);
            var updated = ShiftDto.From(date, ToGuardDto(guard))
                with { SignedOffAt = signedOffAt?.ToUniversalTime().ToString("O") };
            _shifts[date] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task DeleteAsync(DateOnly date, int? expectedGuardId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_shifts.TryGetValue(date, out var current))
            {
                return Task.CompletedTask;
            }

            if (expectedGuardId is null)
            {
                throw Errors.PreconditionRequired();
            }

            if (current.Guard!.Id != expectedGuardId.Value)
            {
                throw Errors.ShiftChanged(current);
            }

            _shifts.Remove(date);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<SwapRequestDto>> GetSwapRequestsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SwapRequestDto>>(_swaps.Values.OrderBy(swap => swap.Date, StringComparer.Ordinal).ToArray());
        }
    }

    public Task<SwapRequestDto> CreateSwapRequestAsync(DateOnly date, DateOnly targetDate, int requesterId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var requester = FindActiveGuard(requesterId);
            if (!_shifts.TryGetValue(date, out var own))
            {
                throw Errors.ShiftNotTaken();
            }

            if (own.Guard!.Id != requesterId)
            {
                throw Errors.ShiftChanged(own);
            }

            if (!_shifts.TryGetValue(targetDate, out var other))
            {
                throw Errors.ShiftNotTaken();
            }

            if (other.Guard!.Id == requesterId)
            {
                throw Errors.SwapInvalid();
            }

            var swap = new SwapRequestDto(ShiftDto.Iso(date), ShiftDto.Iso(targetDate), ToGuardDto(requester), other.Guard);
            _swaps[(date, targetDate)] = swap;
            return Task.FromResult(swap);
        }
    }

    public Task AcceptSwapRequestAsync(DateOnly date, DateOnly targetDate, int guardId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_swaps.TryGetValue((date, targetDate), out var swap))
            {
                throw Errors.SwapRequestNotFound();
            }

            if (swap.Target.Id != guardId)
            {
                throw Errors.SwapForbidden();
            }

            var requester = FindActiveGuard(swap.Requester.Id);
            var target = FindActiveGuard(swap.Target.Id);
            _shifts.TryGetValue(date, out var own);
            _shifts.TryGetValue(targetDate, out var other);
            if (own?.Guard?.Id != swap.Requester.Id)
            {
                _swaps.Remove((date, targetDate));
                throw Errors.ShiftChanged(own ?? ShiftDto.From(date, null));
            }

            if (other?.Guard?.Id != swap.Target.Id)
            {
                _swaps.Remove((date, targetDate));
                throw Errors.ShiftChanged(other ?? ShiftDto.From(targetDate, null));
            }

            _shifts[date] = ShiftDto.From(date, ToGuardDto(target));
            _shifts[targetDate] = ShiftDto.From(targetDate, ToGuardDto(requester));
            _swaps.Remove((date, targetDate));
            return Task.CompletedTask;
        }
    }

    public Task DeleteSwapRequestAsync(DateOnly date, DateOnly targetDate, int guardId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_swaps.TryGetValue((date, targetDate), out var swap))
            {
                return Task.CompletedTask;
            }

            if (guardId != swap.Requester.Id && guardId != swap.Target.Id)
            {
                throw Errors.SwapForbidden();
            }

            _swaps.Remove((date, targetDate));
            return Task.CompletedTask;
        }
    }

    public Task<AdminGuardDto> CreateGuardAsync(string? name, string? phone, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var normalizedName = Normalization.Name(name);
        var normalizedPhone = Normalization.Phone(phone);
        lock (_gate)
        {
            if (_guardIdsByName.ContainsKey(normalizedName.Key))
            {
                throw Errors.GuardNameTaken();
            }

            if (_nextGuardId == int.MaxValue)
            {
                throw new InvalidOperationException("The guard ID counter has exhausted the Int32 range.");
            }

            var guard = new Guard(++_nextGuardId, normalizedName.Name, normalizedName.Key, normalizedPhone, true);
            _guards.Add(guard.Id, guard);
            _guardIdsByName.Add(guard.NameKey, guard.Id);
            return Task.FromResult(ToAdminGuardDto(guard));
        }
    }

    public Task<AdminGuardDto> UpdateGuardAsync(int id, string? name, string? phone, bool? active, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var normalizedName = Normalization.Name(name);
        var normalizedPhone = Normalization.Phone(phone);
        if (active is null)
        {
            throw Errors.InvalidBody();
        }

        lock (_gate)
        {
            if (!_guards.TryGetValue(id, out var current))
            {
                throw Errors.GuardNotFound();
            }

            if (_guardIdsByName.TryGetValue(normalizedName.Key, out var existingId) && existingId != id)
            {
                throw Errors.GuardNameTaken();
            }

            if (!string.Equals(current.NameKey, normalizedName.Key, StringComparison.Ordinal))
            {
                _guardIdsByName.Remove(current.NameKey);
                _guardIdsByName.Add(normalizedName.Key, id);
            }

            var updated = current with
            {
                Name = normalizedName.Name,
                NameKey = normalizedName.Key,
                Phone = normalizedPhone,
                Active = active.Value,
            };
            _guards[id] = updated;
            return Task.FromResult(ToAdminGuardDto(updated));
        }
    }

    public Task DeactivateGuardAsync(int id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_guards.TryGetValue(id, out var guard))
            {
                throw Errors.GuardNotFound();
            }

            _guards[id] = guard with { Active = false };
            return Task.CompletedTask;
        }
    }

    private Guard FindActiveGuard(int id)
    {
        if (!_guards.TryGetValue(id, out var guard) || !guard.Active)
        {
            throw Errors.UnknownGuard();
        }

        return guard;
    }

    private static GuardDto ToGuardDto(Guard guard) => new(guard.Id, guard.Name, guard.Phone);

    private static AdminGuardDto ToAdminGuardDto(Guard guard) =>
        new(guard.Id, guard.Name, guard.Phone, guard.Active);
}