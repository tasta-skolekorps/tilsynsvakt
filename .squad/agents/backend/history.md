# Backend History

- **Owner:** Unavailable (git config user.name could not be read)
- **Project:** Tilsynsvakt guard handbook and roster
- **Stack:** .NET minimal API, Aspire, Azure Container Apps (express); static frontend on GitHub Pages
- **Created:** 2026-10-03T09:07:58.155Z

## Learnings

- Validate roster input and restrict CORS to the frontend origin. Persist only approved shift fields: date, name, and phone.
- The backend is the roster source of truth. Persistence is undecided and must remain an explicit open decision.
- Never call Spond from the backend API; a scheduled GitHub Action performs one-way backend-to-Spond synchronization.
- No authentication is used. Keep incident reports submission-free until delivery is decided, and never include access codes or secrets in repository content.
- Keep signup, replacement, cancellation, and admin mutations inside `BEGIN IMMEDIATE` transactions so validation and writes serialize against concurrent roster changes.
- Map Scalar after OpenAPI in all environments; its unmapped reference endpoint has no mutation/admin rate-limit policy.
- Azure Table mutations must use same-partition entity-group transactions and ETags; inspect the failed transaction action index to distinguish stale shift conflicts from retryable guard updates. This project now uses the approved Table Storage design, but local Table behavior remains unverified when Azurite is unavailable.
- For bounded Azure Table ETag retries, use capped exponential jitter and return a stable 503 ProblemDetails (`storage_busy`, with `Retry-After`) if the budget is exhausted. Keep true duplicate-name 409s immediate; cancel's one-shot stale-ETag outcome remains `shift_changed`. The opt-in Azurite contracts verified three consecutive passes of the 20-way unique-ID allocation test.

📌 Team update (2026-10-03T22:48:50.1676592+02:00): Azure Table Storage is the selected roster persistence; SQLite/Azure Files database assumptions are superseded and SQLite has been removed from the implementation.