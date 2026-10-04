using System.Globalization;
using Azure;
using Azure.Data.Tables;

namespace Tilsynsvakt.Api;

public sealed partial class TableStores
{
    public async Task<IReadOnlyList<SwapRequestDto>> GetSwapRequestsAsync(CancellationToken ct)
    {
        var filter = $"PartitionKey eq '{PartitionKey}' and RowKey ge 'R_' and RowKey lt 'R`'";
        var requests = new List<SwapRequestDto>();
        await foreach (var entity in Table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            requests.Add(ToSwapRequest(entity));
        }

        return requests.OrderBy(request => request.Date, StringComparer.Ordinal).ToArray();
    }

    public async Task<SwapRequestDto> CreateSwapRequestAsync(DateOnly date, DateOnly targetDate, int requesterId, CancellationToken ct)
    {
        var requester = await FindActiveGuardEntityAsync(requesterId, ct) ?? throw Errors.UnknownGuard();
        var own = await ReadEntityAsync(ShiftRowKey(date), ct) ?? throw Errors.ShiftNotTaken();
        if (ReadInt32(own, "GuardId") != requesterId)
        {
            throw Errors.ShiftChanged(ToShift(own, date));
        }

        var other = await ReadEntityAsync(ShiftRowKey(targetDate), ct) ?? throw Errors.ShiftNotTaken();
        if (ReadInt32(other, "GuardId") == requesterId)
        {
            throw Errors.SwapInvalid();
        }

        var entity = NewEntity(SwapRowKey(date, targetDate),
            ("Date", ShiftDto.Iso(date)),
            ("TargetDate", ShiftDto.Iso(targetDate)),
            ("RequesterId", requesterId),
            ("RequesterName", ReadString(requester, "Name")),
            ("RequesterPhone", ReadString(requester, "Phone")),
            ("TargetId", ReadInt32(other, "GuardId")),
            ("TargetName", ReadString(other, "GuardName")),
            ("TargetPhone", ReadString(other, "GuardPhone")));
        await Table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        return ToSwapRequest(entity);
    }

    public async Task AcceptSwapRequestAsync(DateOnly date, DateOnly targetDate, int guardId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxConcurrencyAttempts; attempt++)
        {
            var requestEntity = await ReadEntityAsync(SwapRowKey(date, targetDate), ct) ?? throw Errors.SwapRequestNotFound();
            var request = ToSwapRequest(requestEntity);
            if (request.Target.Id != guardId)
            {
                throw Errors.SwapForbidden();
            }

            var requester = await FindActiveGuardEntityAsync(request.Requester.Id, ct) ?? throw Errors.UnknownGuard();
            var target = await FindActiveGuardEntityAsync(request.Target.Id, ct) ?? throw Errors.UnknownGuard();
            var own = await ReadEntityAsync(ShiftRowKey(date), ct);
            var other = await ReadEntityAsync(ShiftRowKey(targetDate), ct);
            if (own is null || ReadInt32(own, "GuardId") != request.Requester.Id)
            {
                await TryDeleteSwapAsync(requestEntity, ct);
                throw Errors.ShiftChanged(own is null ? ShiftDto.From(date, null) : ToShift(own, date));
            }

            if (other is null || ReadInt32(other, "GuardId") != request.Target.Id)
            {
                await TryDeleteSwapAsync(requestEntity, ct);
                throw Errors.ShiftChanged(other is null ? ShiftDto.From(targetDate, null) : ToShift(other, targetDate));
            }

            try
            {
                await Table.SubmitTransactionAsync(
                [
                    new TableTransactionAction(TableTransactionActionType.UpdateReplace,
                        CloneGuard(requester, active: true, version: ReadInt32(requester, "Version") + 1), requester.ETag),
                    new TableTransactionAction(TableTransactionActionType.UpdateReplace,
                        CloneGuard(target, active: true, version: ReadInt32(target, "Version") + 1), target.ETag),
                    new TableTransactionAction(TableTransactionActionType.UpdateReplace, NewShiftEntity(date, target, null), own.ETag),
                    new TableTransactionAction(TableTransactionActionType.UpdateReplace, NewShiftEntity(targetDate, requester, null), other.ETag),
                    new TableTransactionAction(TableTransactionActionType.Delete, requestEntity, requestEntity.ETag),
                ], ct);
                return;
            }
            catch (RequestFailedException ex) when (IsConditionNotSatisfied(ex) || IsEntityNotFound(ex))
            {
                await DelayBeforeRetryAsync(attempt, ct);
            }
        }

        throw Errors.StorageBusy();
    }

    public async Task DeleteSwapRequestAsync(DateOnly date, DateOnly targetDate, int guardId, CancellationToken ct)
    {
        var entity = await ReadEntityAsync(SwapRowKey(date, targetDate), ct);
        if (entity is null)
        {
            return;
        }

        var request = ToSwapRequest(entity);
        if (guardId != request.Requester.Id && guardId != request.Target.Id)
        {
            throw Errors.SwapForbidden();
        }

        await TryDeleteSwapAsync(entity, ct);
    }

    private async Task TryDeleteSwapAsync(TableEntity entity, CancellationToken ct)
    {
        try
        {
            await Table.DeleteEntityAsync(PartitionKey, entity.RowKey, entity.ETag, ct);
        }
        catch (RequestFailedException ex) when (IsConditionNotSatisfied(ex) || IsEntityNotFound(ex))
        {
            // Already removed or replaced by a newer request.
        }
    }

    private static string SwapRowKey(DateOnly date, DateOnly targetDate) =>
        $"R_{ShiftDto.Iso(date)}_{ShiftDto.Iso(targetDate)}";

    private static SwapRequestDto ToSwapRequest(TableEntity entity) =>
        new(ReadString(entity, "Date"), ReadString(entity, "TargetDate"),
            new GuardDto(ReadInt32(entity, "RequesterId"), ReadString(entity, "RequesterName"), ReadString(entity, "RequesterPhone")),
            new GuardDto(ReadInt32(entity, "TargetId"), ReadString(entity, "TargetName"), ReadString(entity, "TargetPhone")));
}
