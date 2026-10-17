# Squad Decisions

## Active Decisions

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction

### Open items (as of 2026-10-03)

The following remain unverified or are no longer applicable:

- **UNVERIFIED — Azure-hosted Table smoke test:** Run a pre-production smoke test against Azure Table Storage; Azurite coverage does not establish cloud behavior.
- **UNVERIFIED — shared-key-disabled compatibility:** Verify the API and Aspire configuration with storage-account shared-key access disabled.
- **UNVERIFIED — table-scoped RBAC:** Verify the ACA system-assigned identity's table-scoped role assignment and exact SDK data actions, including startup against a pre-created table.
- **UNVERIFIED — scale-to-zero vs per-replica rate limiting:** Validate rate-limit behavior across ACA scale-to-zero and multiple replicas.
- **UNVERIFIED — backup/restore process validation:** Implement and test the Table export/restore process, including a restore drill.
- **UNVERIFIED — forwarded-header trust behind ACA ingress:** Confirm ACA's forwarded-header chain and trusted-hop configuration before relying on client IP rate limiting.
- **Not applicable:** ACA express storage constraints are irrelevant; standard ACA is the selected deployment target.

### 2026-10-16: Table Storage is single source for guards
**By:** repo owner (approved) via Lead
**What:** Guards come only from the backend API (Azure Table Storage), in the alphabetical order returned by the API. `data/guards.json`, `data/roster-import.json`, `scripts/import-roster.ps1` and `js/guards.js` are removed; the roster import was a one-time seed. The frontend caches the `{id, name}` list in localStorage key `tilsynsvakt.guards` as a read-only fallback only. `data/usage-plan.json` and `data/contacts.json` stay as static repo JSON (offline use, per copilot-instructions).
**Why:** `guards.json` duplicated Table guard data and could drift from the roster master.

### 2026-10-17: Simple shared-password access key for the API
**By:** Lead (requested by Leif Bjarte Johansson)
**What:** Replace "trust-based, no authentication" with one shared password. `POST /api/auth/login` `{password}` → `{apiKey}`; key = `"tv1." + Base64Url(HMACSHA256(Access:Password, "tilsynsvakt-api-key-v1"))`, stateless and computed at startup. Frontend stores it in `localStorage["tilsynsvakt.apiKey"]` and sends `X-Api-Key`. Middleware gates every `/api/*` path except `/api/auth/login`, `/api/admin/**` (keeps its own admin credentials) and OPTIONS; 401 code `access_key_required`, wrong password 401 `invalid_password`. Constant-time comparison; login rate limit `RateLimit:LoginPerMinute` (default 10/IP). API refuses to start without `Access:Password` (fail closed). CORS allows `X-Api-Key`. SpondSync logs in with `API_PASSWORD`; GitHub repo secret `ACCESS_PASSWORD` feeds deploy (`Access__Password` → ACA secret via AppHost parameter `access-password`) and the Spond sync workflow. No per-user accounts or personalization; name selection stays a selection.
**Why:** User asked for a "really simple" mechanism so data is not entirely open. Deriving the key from the password needs one secret and no storage; rotating the password revokes all keys. `X-Api-Key` avoids colliding with the admin `Authorization` header so admin.js is untouched.
**Trade-offs:** Key is password-equivalent. Rate-limit partitions may be shared behind ACA (no forwarded headers), so use a password of 12 or more characters.
**Follow-up:** Update the "do not introduce authentication" boundary in the lead/backend/tester charters. Full contract: session file `auth-contract.md`.

### 2026-10-17: Backend: simple shared-password auth
**Source:** decisions/inbox/backend-simple-auth.md

Implemented per `auth-contract.md` §1 and §3 (Backend part). No contract deviations in the interface.

Implementation choices within the contract:
- The derived key and password are held in a new singleton `AccessSettings` (in `AccessKey.cs`). `AccessKeyMiddleware` takes it through DI.
- The gate rejects requests that send `X-Api-Key` more than once (`Count != 1`) with 401 `access_key_required`.
- `AccessKey.Derive` throws `ArgumentException` for null/empty passwords. Startup already rejects these before it runs.
- `BackendClient.LoginAsync` throws the generic `Innlogging mot vakt-API-et mislyktes (HTTP 200).` message when a 2xx body is not JSON or has no non-empty string `apiKey`.
- `RateLimit:LoginPerMinute: 10` was added explicitly to `appsettings.json`.
- Fix: removed `.Accepts<LoginBody>` from the login endpoint. The request body is therefore not described in OpenAPI, but text/plain now returns the `unsupported_media_type` problem body as the contract requires.

### 2026-10-17: Frontend: simple shared-password auth
**Source:** decisions/inbox/frontend-simple-auth.md

Implemented contract §2 in `index.html`, `js/app.js`, `styles.css`, `sw.js` (cache `tilsynsvakt-static-v9`). Admin files untouched.

Small, compatible additions (no contract deviations in the interface):
- `requireLogin()` also clears in-memory API state and renders Norwegian placeholders ("Logg inn for å se vaktlisten.") in roster/handover areas and disables the name picker, so stale roster data is never shown after a 401/logout.
- Login 401/429 map to the contract texts regardless of body; other non-2xx show ProblemDetails title/detail. A 200 without `apiKey` shows «Innloggingen mislyktes. Prøv igjen.».
- If `localStorage` cannot store the key, show «Nettleseren kunne ikke lagre innloggingen. Sjekk at lagring er tillatt.» in `#login-error`.
- The password input is cleared after every submit (success and failure).
- New `.link-button` style (did not exist before) for the footer «Logg ut».

Pre-existing issue noticed, not fixed (out of scope): `#swap-cancel` handler calls `renderSwaps()`, which is not defined in `js/app.js`.

### 2026-10-17: Infra: simple shared-password auth
**Source:** decisions/inbox/infra-simple-auth.md

By: Infra. Requested by Leif Bjarte Johansson. Implements contract §3–§5 (Infra part).

- AppHost publishes `Access:Password` as ACA secret `access-password` → env `Access__Password` (secretRef), via the existing secret-parameter loop.
- AppHost throws in publish mode when `Access:Password` is missing, so a deploy never ships an API that would refuse to start.
- New GitHub secret: `ACCESS_PASSWORD` (repository-level). Deploy maps it to `Access__Password`; Spond sync maps it to `API_PASSWORD`. Both validate it as required.
- Docs: README and `.github/copilot-instructions.md` now describe the shared-password → `X-Api-Key` model instead of "trust-based, no authentication".

Deviations from contract: none.

Follow-up for coordinator: Infra charter still says "Preserve the trust-based, no-authentication product model" — update along with lead/backend/tester charters.
