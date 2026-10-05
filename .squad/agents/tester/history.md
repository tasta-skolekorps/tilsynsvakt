# Tester History

- **Owner:** Unavailable (git config user.name could not be read)
- **Project:** Tilsynsvakt guard handbook and roster
- **Stack:** Static frontend on GitHub Pages; .NET minimal API, Aspire, Azure Container Apps (express)
- **Created:** 2026-10-03T09:07:58.155Z

## Learnings

- Cover roster input validation, shift changes, and the frontend/API contract without assuming a persistence implementation.
- Check that CORS is limited to the frontend origin and no authentication is introduced.
- Spond synchronization is one-way from the backend via a scheduled GitHub Action; browser and API code must not call Spond.
- Incident reporting has no submission path until delivery is decided. Keep test data free of access codes, secrets, and unapproved personal data.
- Contract tests can share one SQLite-backed WebApplicationFactory when successful signup dates are unique and held outside the dedicated October range assertion; use a separate database only for restart-persistence verification.
- After the switch to Azure Tables, replace SQLite fixture plumbing with `IStores`/`IStoreLifecycle` test doubles; opt-in Azurite contracts exposed guard-ID counter ETag retries exhausting under 20 concurrent creates.

📌 Team update (2026-10-03T22:48:50.1676592+02:00): Azure Table Storage is the selected roster persistence; SQLite fixture assumptions are superseded. The latest Table implementation passes the Azurite concurrency contract in three consecutive runs, with cloud smoke testing still open.

- **2026-10-05T09:55:13+02:00, Leif Bjarte Johansson, issue #39:** Added 88 fake-fixture Spond sync pure/helper regressions. Initial marker/no-op hypothesis passed 2/2; final scoped suite 88 passed, 0 failed, 0 skipped; exact `dotnet test Tilsynsvakt.slnx` 125 passed, 0 failed, 2 opt-in Azurite skipped. Oslo invitation subtraction must use wall-clock days across DST, not a fixed 168-hour duration. Verified-state planner idempotence does not establish live idempotence: runtime event reads have null verified state. PublicName can be tested by reflection without invoking the entry point; inline warning/output and exit-policy integration remain unverified. No live services, real personal-data fixtures, production edits or git mutations.

- Team update (2026-10-05T09:55:13+02:00; issue #39; Scribe for Leif Bjarte Johansson): Accepted personal-account exposure and ToS section 5 risk do not authorize guessed Spond payloads. Retain guardian-only Tilsynsvakt scope, configurable 26.11.2026 pilot and seven-day invitation scheduling. Full-sync executable writes remain safety-blocked; Backend's limited metadata HTTP mock is separate from pure planner coverage and is not live response/notification evidence. Manifest reports 88 new tests and 125 passed / 0 failed / 2 skipped overall; Scribe records, but does not rerun, these results.

- Team update (2026-10-05T15:30:00+02:00; issue #39; Scribe): Supplied manifest reports 125 tests passed after relocation and 175 after observed-schema implementation; these are distinct historical snapshots, not new Scribe runs. Canonical evidence now describes guardian profileId matching, quiet delete/create and REMIND_48H_BEFORE. Retain explicit gaps for location without id, CREATE inviteTime, three-day reminder, live idempotence and notification/response preservation; intercepted requests and local tests do not establish server acceptance.