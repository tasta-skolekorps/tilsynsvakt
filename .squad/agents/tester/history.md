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