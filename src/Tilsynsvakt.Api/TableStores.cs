using System.Globalization;
using System.Text;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;

namespace Tilsynsvakt.Api;

public interface IStoreLifecycle
{
    Task InitializeAsync(CancellationToken ct);
    Task CheckReadyAsync(CancellationToken ct);
}

public sealed class TableStores : IStores, IStoreLifecycle
{
    private const string PartitionKey = "roster";
    private const string CounterRowKey = "META_GUARD_ID";
    private const int MaxConcurrencyAttempts = 15;
    private readonly IConfiguration _configuration;
    private readonly Lazy<TableClient> _table;

    public TableStores(IServiceProvider services, IConfiguration configuration)
    {
        _configuration = configuration;
        var tableName = configuration["Storage:TableName"];
        if (string.IsNullOrWhiteSpace(tableName))
        {
            tableName = "Tilsynsvakt";
        }

        _table = new Lazy<TableClient>(() => services.GetRequiredService<TableServiceClient>().GetTableClient(tableName));
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            if (_configuration.GetValue("Storage:CreateTable", false))
            {
                await Table.CreateIfNotExistsAsync(ct);
            }

            try
            {
                await Table.AddEntityAsync(NewEntity(CounterRowKey, ("NextId", 0)), ct);
            }
            catch (RequestFailedException ex) when (IsEntityAlreadyExists(ex))
            {
                // Another API instance initialized the shared counter first.
            }

            if (await ReadEntityAsync(CounterRowKey, ct) is null)
            {
                throw new InvalidOperationException("The guard ID counter is missing from the configured Table.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Azure Table Storage initialization failed. Verify the Storage configuration and table access.", ex);
        }
    }

    public async Task CheckReadyAsync(CancellationToken ct)
    {
        if (await ReadEntityAsync(CounterRowKey, ct) is null)
        {
            throw new InvalidOperationException("The guard ID counter is missing from the configured Table.");
        }
    }

    public async Task<IReadOnlyList<GuardDto>> GetActiveGuardsAsync(CancellationToken ct)
    {
        var guards = await ReadGuardsAsync(ct);
        return guards
            .Where(guard => ReadBoolean(guard, "Active"))
            .OrderBy(guard => ReadString(guard, "NameKey"), StringComparer.Ordinal)
            .Select(guard => ToGuardDto(guard))
            .ToArray();
    }

    public async Task<IReadOnlyList<AdminGuardDto>> GetAllGuardsAsync(CancellationToken ct)
    {
        var guards = await ReadGuardsAsync(ct);
        return guards
            .OrderBy(guard => ReadString(guard, "NameKey"), StringComparer.Ordinal)
            .Select(guard => new AdminGuardDto(
                ReadInt32(guard, "Id"), ReadString(guard, "Name"), ReadString(guard, "Phone"), ReadBoolean(guard, "Active")))
            .ToArray();
    }

    public async Task<IReadOnlyList<ShiftDto>> GetShiftsAsync(DateOnly from, DateOnly to, ShiftCalendar calendar, CancellationToken ct)
    {
        var filter = $"PartitionKey eq '{PartitionKey}' and RowKey ge '{ShiftRowKey(from)}' and RowKey le '{ShiftRowKey(to)}'";
        var taken = new Dictionary<DateOnly, ShiftDto>();
        await foreach (var entity in Table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            var date = DateOnly.ParseExact(ReadString(entity, "Date"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            taken[date] = ToShift(entity, date);
        }

        var shifts = new List<ShiftDto>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (calendar.IsShiftDay(date))
            {
                shifts.Add(taken.GetValueOrDefault(date) ?? ShiftDto.From(date, null));
            }
        }

        return shifts;
    }

    public async Task<ShiftDto?> GetShiftAsync(DateOnly date, CancellationToken ct)
    {
        var entity = await ReadEntityAsync(ShiftRowKey(date), ct);
        return entity is null ? null : ToShift(entity, date);
    }

    public async Task<SignUpResult> SignUpAsync(DateOnly date, int guardId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
        {
            var guard = await FindActiveGuardEntityAsync(guardId, ct) ?? throw Errors.UnknownGuard();
            var currentEntity = await ReadEntityAsync(ShiftRowKey(date), ct);
            if (currentEntity is not null)
            {
                var current = ToShift(currentEntity, date);
                if (current.Guard!.Id == guardId)
                {
                    return new SignUpResult(current, false);
                }

                throw Errors.ShiftTaken(current);
            }

            var changedGuard = CloneGuard(guard, active: true, version: ReadInt32(guard, "Version") + 1);
            try
            {
                await Table.SubmitTransactionAsync(
                [
                    new TableTransactionAction(TableTransactionActionType.UpdateReplace, changedGuard, guard.ETag),
                    new TableTransactionAction(TableTransactionActionType.Add, NewShiftEntity(date, guard)),
                ], ct);
                return new SignUpResult(ShiftDto.From(date, ToGuardDto(guard)), true);
            }
            catch (RequestFailedException ex) when (IsEntityAlreadyExists(ex))
            {
                var latest = await ReadEntityAsync(ShiftRowKey(date), ct);
                if (latest is null)
                {
                    await DelayBeforeRetryAsync(attempt, ct);
                    continue;
                }

                var current = ToShift(latest, date);
                if (current.Guard!.Id == guardId)
                {
                    return new SignUpResult(current, false);
                }

                throw Errors.ShiftTaken(current);
            }
            catch (RequestFailedException ex) when (IsConditionNotSatisfied(ex))
            {
                if (await FindActiveGuardEntityAsync(guardId, ct) is null)
                {
                    throw Errors.UnknownGuard();
                }

                await DelayBeforeRetryAsync(attempt, ct);
            }
        }

        throw Errors.StorageBusy();
    }

    public async Task<ShiftDto> ReplaceAsync(DateOnly date, int guardId, int expectedGuardId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
        {
            var guard = await FindActiveGuardEntityAsync(guardId, ct) ?? throw Errors.UnknownGuard();
            var currentEntity = await ReadEntityAsync(ShiftRowKey(date), ct) ?? throw Errors.ShiftNotTaken();
            var current = ToShift(currentEntity, date);
            if (current.Guard!.Id != expectedGuardId)
            {
                throw Errors.ShiftChanged(current);
            }

            if (current.Guard.Id == guardId)
            {
                return current;
            }

            var changedGuard = CloneGuard(guard, active: true, version: ReadInt32(guard, "Version") + 1);
            try
            {
                await Table.SubmitTransactionAsync(
                [
                    new TableTransactionAction(TableTransactionActionType.UpdateReplace, changedGuard, guard.ETag),
                    new TableTransactionAction(TableTransactionActionType.UpdateReplace, NewShiftEntity(date, guard), currentEntity.ETag),
                ], ct);
                return ShiftDto.From(date, ToGuardDto(guard));
            }
            catch (RequestFailedException ex) when (IsConditionNotSatisfied(ex) || IsEntityNotFound(ex))
            {
                if (await FindActiveGuardEntityAsync(guardId, ct) is null)
                {
                    throw Errors.UnknownGuard();
                }

                var latest = await ReadEntityAsync(ShiftRowKey(date), ct);
                var failedAction = (ex as TableTransactionFailedException)?.FailedTransactionActionIndex;
                if (IsEntityNotFound(ex) || failedAction != 0 || latest is null)
                {
                    throw Errors.ShiftChanged(latest is null ? ShiftDto.From(date, null) : ToShift(latest, date));
                }

                var latestShift = ToShift(latest, date);
                if (latestShift.Guard!.Id != expectedGuardId)
                {
                    throw Errors.ShiftChanged(latestShift);
                }

                await DelayBeforeRetryAsync(attempt, ct);
            }
        }

        throw Errors.StorageBusy();
    }

    public async Task DeleteAsync(DateOnly date, int? expectedGuardId, CancellationToken ct)
    {
        var currentEntity = await ReadEntityAsync(ShiftRowKey(date), ct);
        if (currentEntity is null)
        {
            return;
        }

        var current = ToShift(currentEntity, date);
        if (expectedGuardId is null)
        {
            throw Errors.PreconditionRequired();
        }

        if (current.Guard!.Id != expectedGuardId.Value)
        {
            throw Errors.ShiftChanged(current);
        }

        try
        {
            await Table.DeleteEntityAsync(PartitionKey, ShiftRowKey(date), currentEntity.ETag, ct);
        }
        catch (RequestFailedException ex) when (IsConditionNotSatisfied(ex) || IsEntityNotFound(ex))
        {
            var latest = await ReadEntityAsync(ShiftRowKey(date), ct);
            throw Errors.ShiftChanged(latest is null ? ShiftDto.From(date, null) : ToShift(latest, date));
        }
    }

    public async Task<AdminGuardDto> CreateGuardAsync(string? name, string? phone, CancellationToken ct)
    {
        var normalizedName = Normalization.Name(name);
        var normalizedPhone = Normalization.Phone(phone);

        for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
        {
            var counter = await ReadEntityAsync(CounterRowKey, ct)
                ?? throw new InvalidOperationException("The guard ID counter is missing from the configured Table.");
            var currentId = ReadInt32(counter, "NextId");
            if (currentId == int.MaxValue)
            {
                throw new InvalidOperationException("The guard ID counter has exhausted the Int32 range.");
            }

            var id = currentId + 1;
            var guard = NewGuardEntity(id, normalizedName.Name, normalizedName.Key, normalizedPhone, active: true, version: 1);
            try
            {
                await Table.SubmitTransactionAsync(
                [
                    new TableTransactionAction(TableTransactionActionType.UpdateReplace,
                        NewEntity(CounterRowKey, ("NextId", id)), counter.ETag),
                    new TableTransactionAction(TableTransactionActionType.Add, guard),
                    new TableTransactionAction(TableTransactionActionType.Add, NewNameIndexEntity(normalizedName.Key, id)),
                ], ct);
                return new AdminGuardDto(id, normalizedName.Name, normalizedPhone, true);
            }
            catch (RequestFailedException ex) when (IsEntityAlreadyExists(ex))
            {
                throw Errors.GuardNameTaken();
            }
            catch (RequestFailedException ex) when (IsConditionNotSatisfied(ex))
            {
                // Reread the counter and retry; no ID is consumed until the transaction commits.
                await DelayBeforeRetryAsync(attempt, ct);
            }
        }

        throw Errors.StorageBusy();
    }

    public async Task<AdminGuardDto> UpdateGuardAsync(int id, string? name, string? phone, bool? active, CancellationToken ct)
    {
        var normalizedName = Normalization.Name(name);
        var normalizedPhone = Normalization.Phone(phone);
        if (active is null)
        {
            throw Errors.InvalidBody();
        }

        for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
        {
            var guard = await FindGuardEntityAsync(id, ct) ?? throw Errors.GuardNotFound();
            var oldNameKey = ReadString(guard, "NameKey");
            var changedGuard = NewGuardEntity(id, normalizedName.Name, normalizedName.Key, normalizedPhone,
                active.Value, ReadInt32(guard, "Version") + 1);
            changedGuard.ETag = guard.ETag;
            var actions = new List<TableTransactionAction>();

            if (!string.Equals(oldNameKey, normalizedName.Key, StringComparison.Ordinal))
            {
                var oldIndex = await ReadEntityAsync(NameIndexRowKey(oldNameKey), ct)
                    ?? throw new InvalidOperationException("The guard name index is missing from the configured Table.");
                actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, oldIndex, oldIndex.ETag));
                actions.Add(new TableTransactionAction(TableTransactionActionType.Add, NewNameIndexEntity(normalizedName.Key, id)));
            }

            actions.Add(new TableTransactionAction(TableTransactionActionType.UpdateReplace, changedGuard, guard.ETag));
            try
            {
                await Table.SubmitTransactionAsync(actions, ct);
                return new AdminGuardDto(id, normalizedName.Name, normalizedPhone, active.Value);
            }
            catch (RequestFailedException ex) when (IsEntityAlreadyExists(ex))
            {
                throw Errors.GuardNameTaken();
            }
            catch (RequestFailedException ex) when (IsConditionNotSatisfied(ex))
            {
                if (await FindGuardEntityAsync(id, ct) is null)
                {
                    throw Errors.GuardNotFound();
                }

                await DelayBeforeRetryAsync(attempt, ct);
            }
        }

        throw Errors.StorageBusy();
    }

    public async Task DeactivateGuardAsync(int id, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
        {
            var guard = await FindGuardEntityAsync(id, ct) ?? throw Errors.GuardNotFound();
            var changedGuard = CloneGuard(guard, active: false, version: ReadInt32(guard, "Version") + 1);
            try
            {
                await Table.UpdateEntityAsync(changedGuard, guard.ETag, TableUpdateMode.Replace, ct);
                return;
            }
            catch (RequestFailedException ex) when (IsConditionNotSatisfied(ex))
            {
                if (await FindGuardEntityAsync(id, ct) is null)
                {
                    throw Errors.GuardNotFound();
                }

                await DelayBeforeRetryAsync(attempt, ct);
            }
        }

        throw Errors.StorageBusy();
    }

    private TableClient Table => _table.Value;

    private async Task<List<TableEntity>> ReadGuardsAsync(CancellationToken ct)
    {
        var filter = $"PartitionKey eq '{PartitionKey}' and RowKey ge 'G_0000000001' and RowKey le 'G_2147483647'";
        var guards = new List<TableEntity>();
        await foreach (var guard in Table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            guards.Add(guard);
        }

        return guards;
    }

    private Task<TableEntity?> FindGuardEntityAsync(int id, CancellationToken ct) =>
        ReadEntityAsync(GuardRowKey(id), ct);

    private async Task<TableEntity?> FindActiveGuardEntityAsync(int id, CancellationToken ct)
    {
        var guard = await FindGuardEntityAsync(id, ct);
        return guard is not null && ReadBoolean(guard, "Active") ? guard : null;
    }

    private async Task<TableEntity?> ReadEntityAsync(string rowKey, CancellationToken ct)
    {
        var response = await Table.GetEntityIfExistsAsync<TableEntity>(PartitionKey, rowKey, cancellationToken: ct);
        return response.HasValue ? response.Value : null;
    }

    private static TableEntity NewShiftEntity(DateOnly date, TableEntity guard) =>
        NewEntity(ShiftRowKey(date),
            ("Date", ShiftDto.Iso(date)),
            ("GuardId", ReadInt32(guard, "Id")),
            ("GuardName", ReadString(guard, "Name")),
            ("GuardPhone", ReadString(guard, "Phone")));

    private static TableEntity NewGuardEntity(int id, string name, string nameKey, string phone, bool active, int version) =>
        NewEntity(GuardRowKey(id),
            ("Id", id),
            ("Name", name),
            ("NameKey", nameKey),
            ("Phone", phone),
            ("Active", active),
            ("Version", version));

    private static TableEntity CloneGuard(TableEntity guard, bool active, int version)
    {
        var changed = NewGuardEntity(
            ReadInt32(guard, "Id"), ReadString(guard, "Name"), ReadString(guard, "NameKey"),
            ReadString(guard, "Phone"), active, version);
        changed.ETag = guard.ETag;
        return changed;
    }

    private static TableEntity NewNameIndexEntity(string nameKey, int guardId) =>
        NewEntity(NameIndexRowKey(nameKey), ("NameKey", nameKey), ("GuardId", guardId));

    private static TableEntity NewEntity(string rowKey, params (string Name, object Value)[] properties)
    {
        var entity = new TableEntity(PartitionKey, rowKey);
        foreach (var (name, value) in properties)
        {
            entity[name] = value;
        }

        return entity;
    }

    private static ShiftDto ToShift(TableEntity entity, DateOnly date) =>
        ShiftDto.From(date, new GuardDto(
            ReadInt32(entity, "GuardId"), ReadString(entity, "GuardName"), ReadString(entity, "GuardPhone")));

    private static GuardDto ToGuardDto(TableEntity entity) =>
        new(ReadInt32(entity, "Id"), ReadString(entity, "Name"), ReadString(entity, "Phone"));

    private static string ReadString(TableEntity entity, string property) => (string)entity[property];

    private static int ReadInt32(TableEntity entity, string property) =>
        Convert.ToInt32(entity[property], CultureInfo.InvariantCulture);

    private static bool ReadBoolean(TableEntity entity, string property) =>
        Convert.ToBoolean(entity[property], CultureInfo.InvariantCulture);

    private static string GuardRowKey(int id) => $"G_{id:D10}";

    private static string ShiftRowKey(DateOnly date) => $"S_{ShiftDto.Iso(date)}";

    private static string NameIndexRowKey(string nameKey)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(nameKey));
        return $"N_{encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    private static bool IsEntityAlreadyExists(RequestFailedException ex) =>
        ex.Status == 409 && string.Equals(ex.ErrorCode, "EntityAlreadyExists", StringComparison.Ordinal);

    private static bool IsConditionNotSatisfied(RequestFailedException ex) =>
        ex.Status == 412 && string.Equals(ex.ErrorCode, "UpdateConditionNotSatisfied", StringComparison.Ordinal);

    private static bool IsEntityNotFound(RequestFailedException ex) =>
        ex.Status == 404 && string.Equals(ex.ErrorCode, "EntityNotFound", StringComparison.Ordinal);

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken ct)
    {
        var maxDelayMilliseconds = Math.Min(80, 5 * (1 << Math.Min(attempt, 4)));
        return Task.Delay(Random.Shared.Next(1, maxDelayMilliseconds + 1), ct);
    }
}