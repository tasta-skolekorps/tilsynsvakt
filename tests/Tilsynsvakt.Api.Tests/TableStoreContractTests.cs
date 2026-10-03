using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tilsynsvakt.Api;

namespace Tilsynsvakt.Api.Tests;

public sealed class AzuriteFactAttribute : FactAttribute
{
    public AzuriteFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TILSYNSVAKT_AZURITE"), "1", StringComparison.Ordinal))
        {
            Skip = "Set TILSYNSVAKT_AZURITE=1 to run the Azurite Table Storage contract test.";
        }
    }
}

public sealed class TableStoreContractTests
{
    private const string AzuriteConnectionString = "UseDevelopmentStorage=true";
    private const string TestPhone = "20000000";

    [AzuriteFact]
    public async Task Table_store_honors_atomicity_preconditions_uniqueness_and_lifecycle()
    {
        var tableName = $"Tilsynsvakt{Guid.NewGuid():N}";
        var tableClient = new TableServiceClient(AzuriteConnectionString).GetTableClient(tableName);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:TableName"] = tableName,
                ["Storage:CreateTable"] = "true",
            })
            .Build();
        using var services = CreateTableClientServices();
        var stores = new TableStores(services, configuration);

        try
        {
            await stores.InitializeAsync(CancellationToken.None);

            var allocated = new List<AdminGuardDto>();
            for (var index = 0; index < 20; index++)
            {
                allocated.Add(await stores.CreateGuardAsync(
                    $"Concurrent Guard {(char)('A' + index)}", TestPhone, CancellationToken.None));
            }

            var duplicate = await Assert.ThrowsAsync<ApiException>(() =>
                stores.CreateGuardAsync("CONCURRENT GUARD A", TestPhone, CancellationToken.None));
            Assert.Equal("guard_name_taken", duplicate.Code);

            var raceDate = new DateOnly(2027, 1, 5);
            var raceResults = await Task.WhenAll(allocated.Select(async guard =>
            {
                try
                {
                    return (Guard: guard, Result: await stores.SignUpAsync(raceDate, guard.Id, CancellationToken.None), Error: (ApiException?)null);
                }
                catch (ApiException error)
                {
                    return (Guard: guard, Result: (SignUpResult?)null, Error: error);
                }
            }));
            var winner = Assert.Single(raceResults, result => result.Result?.Created == true);
            Assert.Equal(19, raceResults.Count(result => result.Error?.Code == "shift_taken"));
            Assert.All(raceResults.Where(result => result.Error?.Code == "shift_taken"), result =>
                Assert.Equal(winner.Guard.Id, result.Error!.CurrentShift!.Guard!.Id));

            var repeat = await stores.SignUpAsync(raceDate, winner.Guard.Id, CancellationToken.None);
            Assert.False(repeat.Created);

            var replacement = allocated.First(guard => guard.Id != winner.Guard.Id);
            var staleReplace = await Assert.ThrowsAsync<ApiException>(() =>
                stores.ReplaceAsync(raceDate, replacement.Id, replacement.Id, CancellationToken.None));
            Assert.Equal("shift_changed", staleReplace.Code);
            Assert.Equal(winner.Guard.Id, staleReplace.CurrentShift!.Guard!.Id);

            var replaced = await stores.ReplaceAsync(raceDate, replacement.Id, winner.Guard.Id, CancellationToken.None);
            Assert.Equal(replacement.Id, replaced.Guard!.Id);

            var staleCancel = await Assert.ThrowsAsync<ApiException>(() =>
                stores.DeleteAsync(raceDate, winner.Guard.Id, CancellationToken.None));
            Assert.Equal("shift_changed", staleCancel.Code);
            var missingPrecondition = await Assert.ThrowsAsync<ApiException>(() =>
                stores.DeleteAsync(raceDate, null, CancellationToken.None));
            Assert.Equal("precondition_required", missingPrecondition.Code);
            await stores.DeleteAsync(raceDate, replacement.Id, CancellationToken.None);
            Assert.Null(await stores.GetShiftAsync(raceDate, CancellationToken.None));
            await stores.DeleteAsync(raceDate, null, CancellationToken.None);

            var deactivationGuard = allocated.First(guard => guard.Id != winner.Guard.Id && guard.Id != replacement.Id);
            var deactivationDate = new DateOnly(2027, 1, 6);
            var signupRace = CaptureApiErrorAsync(() =>
                stores.SignUpAsync(deactivationDate, deactivationGuard.Id, CancellationToken.None));
            var deactivationRace = stores.DeactivateGuardAsync(deactivationGuard.Id, CancellationToken.None);
            var signupError = await signupRace;
            await deactivationRace;
            Assert.True(signupError is null || signupError.Code == "unknown_guard");

            var existingShift = await stores.GetShiftAsync(deactivationDate, CancellationToken.None);
            if (existingShift is not null)
            {
                Assert.Equal(deactivationGuard.Id, existingShift.Guard!.Id);
                Assert.Equal(deactivationGuard.Name, existingShift.Guard.Name);
                Assert.Equal(deactivationGuard.Phone, existingShift.Guard.Phone);
            }

            var signupAfterDeactivation = await Assert.ThrowsAsync<ApiException>(() =>
                stores.SignUpAsync(new DateOnly(2027, 1, 7), deactivationGuard.Id, CancellationToken.None));
            Assert.Equal("unknown_guard", signupAfterDeactivation.Code);

            var persistenceDate = new DateOnly(2027, 1, 8);
            await stores.SignUpAsync(persistenceDate, replacement.Id, CancellationToken.None);
            using var reopenedServices = CreateTableClientServices();
            var reopenedStores = new TableStores(reopenedServices, configuration);
            await reopenedStores.CheckReadyAsync(CancellationToken.None);
            Assert.Equal(20, (await reopenedStores.GetAllGuardsAsync(CancellationToken.None)).Count);
            Assert.Equal(replacement.Id, (await reopenedStores.GetShiftAsync(persistenceDate, CancellationToken.None))?.Guard?.Id);
        }
        finally
        {
            try
            {
                await tableClient.DeleteAsync();
            }
            catch (RequestFailedException error) when (error.Status == 404)
            {
            }
        }
    }

    [AzuriteFact]
    public async Task Concurrent_guard_id_allocation_returns_unique_ids()
    {
        var tableName = $"Tilsynsvakt{Guid.NewGuid():N}";
        var tableClient = new TableServiceClient(AzuriteConnectionString).GetTableClient(tableName);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:TableName"] = tableName,
                ["Storage:CreateTable"] = "true",
            })
            .Build();
        using var services = CreateTableClientServices();
        var stores = new TableStores(services, configuration);

        try
        {
            await stores.InitializeAsync(CancellationToken.None);
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async index =>
            {
                try
                {
                    var guard = await stores.CreateGuardAsync(
                        $"Parallel Guard {(char)('A' + index)}", TestPhone, CancellationToken.None);
                    return (Guard: guard, Error: (Exception?)null);
                }
                catch (Exception error)
                {
                    return (Guard: (AdminGuardDto?)null, Error: error);
                }
            }));

            Assert.True(results.All(result => result.Error is null),
                string.Join(Environment.NewLine, results.Where(result => result.Error is not null).Select(result => result.Error)));
            Assert.Equal(20, results.Select(result => result.Guard!.Id).Distinct().Count());
        }
        finally
        {
            try
            {
                await tableClient.DeleteAsync();
            }
            catch (RequestFailedException error) when (error.Status == 404)
            {
            }
        }
    }

    private static async Task<ApiException?> CaptureApiErrorAsync(Func<Task<SignUpResult>> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (ApiException error)
        {
            return error;
        }
    }

    private static ServiceProvider CreateTableClientServices() => new ServiceCollection()
        .AddSingleton(new TableServiceClient(AzuriteConnectionString))
        .BuildServiceProvider();
}