# Lead History

- **Owner:** Unavailable (git config user.name could not be read)
- **Project:** Tilsynsvakt guard handbook and roster
- **Stack:** Mobile-first static frontend on GitHub Pages; .NET minimal API with Aspire and Azure Container Apps (express)
- **Created:** 2026-10-03T09:07:58.155Z

## Learnings

- The backend is the roster master; scheduled synchronization to Spond is one-way, and Spond is never called from the browser or backend API.
- Persistence is undecided. Keep that choice open and do not imply that a key box is established.
- The service has no authentication. Preserve frontend-origin CORS restrictions and keep incident reporting as a form only until delivery is decided.
- The guard is responsible for the gym hall only. Keep access codes and secrets out of repository content; user-facing text is Norwegian Bokmål.
- 2026-10-03: Azure Table entity-group transactions and ETags can preserve roster uniqueness and conditional signup/replace/cancel behavior across overlapping ACA revisions; keep HTTP tests in-memory and isolate Azurite contract tests because its Table service is Preview.

📌 Team update (2026-10-03T22:48:50.1676592+02:00): Standard ACA with Azure Table Storage is the resolved deployment/persistence choice; the SQLite roster migration question is closed because no deployed data needs migration.