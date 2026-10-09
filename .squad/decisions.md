# Squad Decisions

## Active Decisions

### 2026-10-03: Static frontend API and deployment contract
**By:** Frontend (requested by Leif Bjarte Johansson)
**What:** The public static frontend uses numeric guard IDs from `GET /api/guards`; shift signup, replacement, and cancellation use `POST /api/shifts/{date}/signup`, `PUT /api/shifts/{date}`, and `DELETE /api/shifts/{date}?expectedGuardId={id}` respectively. The Pages API origin must be configured as `Frontend:Origin=https://tasta-skolekorps.github.io`; the API's `appsettings.json` does not set this value. The frontend deployment contract remains `web/config.js` with `window.TILSYNSVAKT_CONFIG = { apiBaseUrl: "..." }`. The available 2025–2026 usage plan is treated as expired after 29.05.2026, while live roster dates remain API-controlled.
**Why:** Verified against the current API models/endpoints and project instructions while implementing issue #3. The GitHub Pages subpath does not form part of the CORS origin.

### 2026-10-03: Pages deployment configuration contract
**By:** Infra (requested by Leif Bjarte Johansson)
**What:**
#### Pages deployment configuration contract
- The repository Actions variable `API_BASE_URL` supplies the public API base URL to the Pages build.
- Before uploading the static site, the workflow overwrites `web/config.js` with `window.TILSYNSVAKT_CONFIG` containing that URL, JSON-encoded from the environment to avoid shell/code injection.
- The variable is public configuration, not a secret. An empty value fails the build before artifact upload.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction

### 2026-10-03: User directive (issue tracking)
**By:** repo owner (via Copilot)
**What:** Always create the GitHub issue for requested work before implementing it. The team creates issues with `gh`; the user does not want to create issues manually.
**Why:** User request — captured for team memory

### 2026-10-03: Backend and Product Constraints

**By:** User (via Copilot)
**What:** Preserve the following authorized project constraints when making backend and adjacent implementation choices.

#### Established


#### Unresolved

Superseded by `.squad/decisions/inbox/copilot-directive-2026-10-03-persistence.md`: backend persistence is SQLite on a mounted volume, accessed via EF Core.

### 2026-10-03: User directive (SQLite persistence)
**Status:** Superseded by 2026-10-03: Switch persistence from SQLite to Azure Table Storage (2026-10-03).
**By:** repo owner (via Copilot)
**What:** Backend persistence is SQLite on a mounted volume, accessed via EF Core. Backend is a .NET minimal API orchestrated with Aspire and deployed to Azure Container Apps (express). The backend replaces the open Google Sheet as roster master; Spond is synced one-way from the backend by a scheduled GitHub Action.
**Why:** User decision — captured for team memory

### 2026-10-03: Guard shift API design
**By:** Lead

> Metadata note: The requested `Get-Date -Format o` and `git config user.name` could not be run in this session (no command-execution tool is available). The session date is known, but the exact timestamp and requester name are not. No values have been fabricated.

#### 1. Solution layout

```text
Tilsynsvakt.slnx                         # Backend alone edits this file
src/Tilsynsvakt.AppHost/                 # .NET Aspire AppHost
src/Tilsynsvakt.ServiceDefaults/          # Aspire health checks, telemetry, defaults
src/Tilsynsvakt.Api/                     # .NET 10 minimal API, SQL and migrations
tests/Tilsynsvakt.Api.Tests/              # API integration tests
```

AppHost references ServiceDefaults and Api. Tests reference Api. No frontend project is introduced.

#### 2. Data model
**Status:** Superseded by 2026-10-03: Azure Table Storage implementation design (2026-10-03).

Use SQLite tables `Guards(Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, NameKey TEXT NOT NULL UNIQUE, Phone TEXT NOT NULL, Active INTEGER NOT NULL DEFAULT 1 CHECK (Active IN (0,1)))` and `Shifts(ShiftDate TEXT PRIMARY KEY, GuardId INTEGER NOT NULL REFERENCES Guards(Id) ON DELETE RESTRICT, GuardName TEXT NOT NULL, GuardPhone TEXT NOT NULL) WITHOUT ROWID`. Store dates as ISO `yyyy-MM-dd`; enforce valid dates and phone/name rules in the API. The date primary key permits at most one signup per shift day. Compute `NameKey` in this order: NFC-normalize, trim and collapse consecutive ASCII spaces, `.ToUpperInvariant()`, then NFC-normalize again. Its unique constraint prevents duplicate names and concurrent insert races. `GuardId` resolves the selected guard; name/phone snapshots preserve what was published for that signup if an admin later edits the guard. These are the only personal fields stored. Deactivate guards instead of deleting them; the FK also prevents accidental deletion of a referenced guard.

#### 3. Calendar

Keep recurring school-year periods in configuration, not duplicated in clients: `Calendar:Periods` = Sep 1–Nov 28 and Jan 5–May 29. Generate eligible Tue–Thu dates (inclusive endpoints) for the relevant school year; Monday board representatives are excluded and Friday has no shifts. Reject other dates for mutations. `GET /api/shifts` enumerates eligible dates in the requested range and joins a signup when present; no row means open. Use `TimeProvider` plus `Europe/Oslo` for “today” and past-date decisions. The API accepts and returns date-only `yyyy-MM-dd`; no UTC conversion is applied to a date.

#### 4. API contract

JSON uses camelCase. `guard` is `{ "id": 12, "name": "Example Name", "phone": "+4792423946" }`; phone is canonical E.164 in API/storage and formatted for display by the frontend.

- `GET /api/guards` → `200 [{id,name,phone}]` for active guards only.
- `GET /api/shifts?from=yyyy-MM-dd&to=yyyy-MM-dd` → `200 {from,to,shifts:[{date,dayOfWeek,status,guard}]}`. `status` is `open` or `taken`; `guard` is null when open. Defaults: today through today + 90 days; maximum inclusive range 366 days. Reads may include historical dates; there is no mutation-window limit on reads. Invalid/reversed/out-of-range query dates → `400`.
- `GET /api/shifts/{date}` → `200 {date,dayOfWeek,status,guard}`; malformed date `400`; valid but ineligible date `404` with `code=not_a_shift_day`.
- `POST /api/shifts/{date}/signup` body `{ "guardId": 12 }` → `201` with the created shift and `Location`; same guard already holds it → `200` idempotently; other guard holds it → `409` (`shift_taken`) with current shift. Malformed JSON/missing or wrongly typed `guardId` → `400`; unknown/inactive guard or date business rule → `422`; non-JSON content type → `415`; body over 2 KB → `413`; throttled → `429`. Do not silently overwrite: trust-based access is not a reason to lose another signup. A single INSERT relies on the date PK to resolve races.
- `PUT /api/shifts/{date}` body `{ "guardId": 12, "expectedGuardId": 9 }` (both required) → `200` updated shift; if both ids match the current occupant, return `200` unchanged only after date eligibility/window and active-target validation pass; otherwise past/ineligible date or inactive target returns `422`. Any eligible but open date `404` (`shift_not_taken`), even if an expected id was supplied; stale expected occupant on a taken date `409` (`shift_changed`). Malformed JSON/missing or wrongly typed fields → `400`; unknown guard → `422`; non-JSON content type `415`; body over 2 KB `413`; throttled `429`. This is an explicit replacement with optimistic concurrency.
- `DELETE /api/shifts/{date}?expectedGuardId=9` → `204`; already-open eligible date → `204`; taken shift without expected id → `428`; stale occupant → `409` (`shift_changed`). Non-integer expected id → `400`; malformed date → `400`; ineligible/past/too-far date → `422`; throttled `429`. Deletes compare the expected occupant in the same transaction.

Malformed JSON/date or invalid range is `400`; business validation is `422`; not found is `404`; concurrent/taken conflicts are `409`. Updates/deletes compare the expected occupant and execute transactionally.

#### 5. Admin guard list

Use `/api/admin/guards` endpoints protected by an `Authorization: Bearer <key>` admin API key configured as an Azure Container Apps secret and injected as `Admin:ApiKey`; local development uses .NET User Secrets. Never put the key in the repo, frontend, or a public GitHub Pages bundle. If unset, do not map admin endpoints (requests receive `404`). Admin operations are performed by a trusted operator tool, not the public frontend. Constant-time key comparison and HTTPS are required. Rate-limit all admin endpoints and shift mutations; return `429` when limited. Key limits on trusted client IP; trust forwarded headers only from a configured proxy, and verify ACA's forwarding behavior before production.

- `GET /api/admin/guards` → `200` all guards including inactive; missing/invalid key `401`; rate limited `429`.
- `POST /api/admin/guards` body `{name,phone}` → `201`; malformed body `400`, invalid fields `422`, duplicate normalized name `409`, missing/invalid key `401`, non-JSON content type `415`, body over 2 KB `413`, rate limited `429`.
- `PUT /api/admin/guards/{id}` body `{name,phone,active}` → `200`; non-integer id or malformed body `400`, invalid fields `422`, missing guard `404`, duplicate normalized name `409`, missing/invalid key `401`, non-JSON content type `415`, body over 2 KB `413`, rate limited `429`. Perform updates in a write transaction; existing shift snapshots are unchanged.
- `DELETE /api/admin/guards/{id}` → `204`, implemented as deactivation; non-integer id `400`, missing guard `404`, missing/invalid key `401`, rate limited `429`. Existing shift snapshots remain taken and unchanged; deactivation blocks new signups by that guard but does not cancel existing shifts. Any caller can still change/cancel one using its current `expectedGuardId`; an inactive guard cannot be the replacement `guardId`. Same-guard idempotent signup returns `200` only while that guard remains active.

Admin endpoints are excluded from OpenAPI. The guard roster is managed only here; there is no self-registration or inference from shifts.

#### 6. Validation and errors

Validate UTF-16 scalar validity before any normalization; reject unpaired surrogates as `invalid_name` (and invalid phone characters as `invalid_phone`). For names, trim, NFC-normalize, and collapse repeated ASCII spaces; require 2–80 Unicode scalar values. First scalar must be a Unicode letter; subsequent scalars may be Unicode letters or combining marks, ASCII space, apostrophe, period, or hyphen. This exact normalized value is stored in `Name` and used to derive `NameKey` as specified in section 2; reject duplicates. Accept Norwegian 8-digit numbers, optionally prefixed by `+47` or `0047`, with ASCII spaces/hyphens; normalize to `+47` plus eight digits, first digit 2–9. Store only normalized phone. Require JSON content type and cap request bodies at 2 KB. Validate active guard, calendar eligibility, and date window (today through today + 400 days); past dates are read-only.

Return RFC 9457 `ProblemDetails` (`application/problem+json`) with stable extension `code`, Norwegian `title`/`detail`, and no stack traces. `shift_taken` and `shift_changed` include extension `currentShift` with the current `{date,dayOfWeek,status,guard}` representation; if no row remains, use the open representation (`status:"open",guard:null`), never null. Example: `{"type":"about:blank","title":"Ugyldig dato","status":400,"detail":"Bruk datoformatet yyyy-MM-dd.","code":"invalid_date"}`. Codes include `invalid_date`, `invalid_range`, `invalid_body`, `invalid_id`, `invalid_name`, `invalid_phone`, `not_a_shift_day`, `date_in_past`, `date_too_far_ahead`, `unknown_guard`, `guard_not_found`, `guard_name_taken`, `shift_not_taken`, `shift_taken`, `shift_changed`, `unauthorized`, `unsupported_media_type`, `request_too_large`, `precondition_required`, and `rate_limited`.

#### 7. Data access and schema lifecycle
**Status:** Superseded by 2026-10-03: Azure Table Storage implementation design (2026-10-03).

Use `Microsoft.Data.Sqlite` with small parameterized SQL statements; no ORM is needed for two tables and simple queries. Keep ordered, embedded SQL migrations (`001_init.sql`, etc.), apply each in a transaction at startup, and track version with `PRAGMA user_version`. Fail startup if the database schema is newer than the API. Enable foreign keys and a busy timeout on every connection. Signup/replacement must use `BEGIN IMMEDIATE`, select the active guard, and copy that guard's id, name, and phone into the shift within the same transaction; shift cancellation uses `BEGIN IMMEDIATE` for its occupant check/delete; admin create/update/deactivation operations also use `BEGIN IMMEDIATE`, so eligibility/deactivation and uniqueness checks serialize.

#### 8. ACA and SQLite hosting
**Status:** Superseded by 2026-10-03: Use Azure Table Storage for standard ACA persistence (2026-10-03).

Configure `Database:Path`, defaulting in the container to `/data/tilsynsvakt.db`. Local AppHost supplies `Database__Path=/data/tilsynsvakt.db` and bind-mounts a repo-local `.data` directory to `/data`; exclude the local database from source control. Do not use an ephemeral container filesystem for production data.

Run one API replica only (min/max replicas 1); SQLite file locking is not a multi-replica coordination mechanism. Prefer `journal_mode=DELETE` and `synchronous=FULL` on a network-mounted share; use `busy_timeout` and short transactions. Do not enable WAL unless the selected filesystem's shared-memory/locking semantics are confirmed. Back up using SQLite's online backup mechanism or a quiesced copy to separate protected storage, and test restore; a volume alone is not a backup.

**⚠️ Unverified:** The available research reports that ACA “express” offers `EmptyDir` only and not persistent Azure Files mounts, but no directly confirmed Microsoft documentation or test was available in this session. `EmptyDir` data would not meet the durable SQLite requirement. The recommended deployment is standard ACA with a supported persistent Azure Files mount, pending confirmation that this target is available and appropriate.

**⚠️ Unverified:** Azure Files SMB locking behavior for SQLite, whether `nobrl` is supported/needed, mount options, and safe behavior during ACA revisions/rollouts were not verified. Validate these against current Azure documentation and a concurrency/restart test before production. If reliable locking cannot be established, retain SQLite only with a supported local persistent disk or revisit persistence.

**⚠️ Unverified:** ACA plan-specific volume support, backup/snapshot options, and exact Aspire AppHost bind-mount API/version must be checked against the selected ACA environment and Aspire version before implementation.

#### 9. CORS, health, and OpenAPI

Read the sole allowed origin from configuration, e.g. `Frontend:Origin=https://<owner>.github.io` (origin has no repository path). No wildcard, credentials, or arbitrary origins. Allow only required methods and `Content-Type`. Use Aspire ServiceDefaults for telemetry and health-check registration, and explicitly map `/health` readiness and `/alive` liveness in every environment (do not rely on development-only template mappings). Tag the DB check `ready` and a process-only check `live`; `/health` runs tagged `ready` checks and returns `503` if any fail, while `/alive` runs only `live` checks and does not require the DB. Expose OpenAPI at `/openapi/v1.json`; do not include admin endpoints or secrets in its document.

#### 10. Spond sync

The scheduled GitHub Action reads `GET /api/shifts?from=...&to=...` and uses taken shifts to create/update one event per shift. Sync is one-way. The API never calls Spond; Spond credentials exist only in GitHub Actions Secrets.

#### 11. Tester strategy

Use `WebApplicationFactory<Program>` with a unique temporary SQLite file per test fixture (not `:memory:` when connections may vary), test configuration for CORS/admin key, and a fake `TimeProvider` set to Europe/Oslo. Cover period boundaries and weekdays; open/taken range results and date formats; signup, idempotence, taken conflict and parallel signup race; change/cancel and stale expected guard; invalid/inactive guard and malformed name/phone/date; admin key absent/valid/invalid and deactivation; migration/restart persistence; ProblemDetails Norwegian text; CORS allowed/denied origins; health with and without DB.

#### 12. Blocking question
**Status:** Closed (2026-10-03): standard ACA with Azure Table Storage was selected; see 2026-10-03: Switch persistence from SQLite to Azure Table Storage.

Confirm the deployment target: **standard ACA with a supported persistent volume** (recommended for the fixed SQLite choice), or ACA “express” with a different persistence choice. Current evidence does not verify that express can provide the required durable mount, so do not start production deployment on an assumption.

### 2026-10-03: Keep the local AppHost database path process-based
**By:** Assigned coding worker, requested by Leif Bjarte Johansson
**What:** Local AppHost runs the API as a project process and sets `Database__Path` to `src/Tilsynsvakt.AppHost/.data/tilsynsvakt.db`; the container default remains `/data/tilsynsvakt.db`. The local file path is ignored by Git.
**Why:** A process-launched project has no container mount point. Using a repo-local persisted file preserves local development data without changing production container storage assumptions. ACA volume availability and SQLite locking behavior remain unverified.

### 2026-10-03: API contract test findings
**By:** Tester
**What:** Added tests around the specified route behavior, validation codes, persistence restart, concurrency, CORS, and normalization.
**Why:** A valid but ineligible single-shift lookup is explicitly 404 (`not_a_shift_day`), while mutation of an ineligible date is 422. The strategy asks for health coverage “with and without DB” but does not define a DB-less fixture/setup; the requested health/alive 200 checks are covered with the configured database. No implementation-specific bugs can be confirmed yet because the route and host files were not present when these tests were authored.

### 2026-10-03: Deployment target is standard Azure Container Apps
**Status:** The standard ACA target remains active; the SQLite/Azure Files and single-replica persistence assumptions are superseded by 2026-10-03: Switch persistence from SQLite to Azure Table Storage (2026-10-03).
**By:** user (via Copilot)
**What:** Use standard Azure Container Apps (not the "express" tier) for the backend. This resolves the Lead's blocking question in lead-api-design.md section 12: SQLite lives on a persistent Azure Files volume mount, single replica (min/max 1).
**Why:** User decision. Express was not verified to offer a durable mount, which the SQLite persistence choice requires. Azure Files SMB locking behavior for SQLite, mount options (e.g. nobrl) and rollout behavior remain to be verified before production.

### 2026-10-03: Standard ACA deployment path and SQLite risks
**Status:** The SQLite/Azure Files volume and single-replica persistence assumptions are superseded by 2026-10-03: Use Azure Table Storage for standard ACA persistence (2026-10-03); standard ACA remains the deployment target.
**By:** user (via Infra)
**What:** Target standard Azure Container Apps with one replica (min/max 1), SQLite on Azure Files mounted at `/data`, a secret-backed `Admin__ApiKey`, the GitHub Pages origin in `Frontend__Origin`, HTTPS-only ingress, and `/alive` liveness plus `/health` readiness probes. Use `infra/` Bicep for deployment rather than adding partial ACA publishing code to the current AppHost.
**Why:** Official Aspire documentation establishes ACA project-resource customization and documents named-volume/bind mounts becoming Azure Files mounts. The exact cached Aspire.Hosting.Azure.AppContainers 13.6.0 XML also documents `Aspire.Hosting.AzureContainerAppProjectExtensions.PublishAsAzureContainerApp<T>(IResourceBuilder<T>, Action<AzureResourceInfrastructure, ContainerApp>)` for project resources. Its exact Azure.Provisioning.AppContainers 1.2.0 dependency exposes `ContainerApp.Template.Volumes`, `ContainerAppVolume`, `ContainerAppVolumeMount`, and `ContainerAppManagedEnvironmentStorage` models. However, local package/API inspection does not establish the complete supported 13.6 configuration that provisions and connects an Azure Files share to this ProjectResource's published ACA app. Therefore Bicep remains the conservative verified path, not because a ProjectResource cannot mount or publish. The unchanged AppHost baseline build succeeds; it does not verify this configuration.

For this project, use Bicep to define the ACA environment's Azure Files storage, the app's volume and mount at `/data`, secret references, `minReplicas`/`maxReplicas` of 1, `allowInsecure: false`, and HTTP probes on `/alive` and `/health`. If revisiting Aspire publishing, first establish the exact 13.6 wiring from the project-resource publish callback through Azure Files environment storage to the app volume, then verify generated artifacts without provisioning.

**GitHub Actions deployment outline:** Use a deployment workflow with `contents: read` and `id-token: write`, authenticate with `azure/login` using a GitHub OIDC federated credential scoped to the repository/ref or protected environment, build and publish the API image, then deploy `infra/` Bicep and update the ACA app. Do not store a client secret or other long-lived Azure credential in the repository. Configure these repository variables: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_LOCATION`, `AZURE_RESOURCE_GROUP`, and `FRONTEND_ORIGIN` (origin only, no path). Configure `ADMIN_API_KEY` as a repository secret and inject it into the ACA secret without logging or materializing its value in artifacts. Keep the scheduled, one-way Spond-sync workflow separate; it reads the backend roster and uses Spond credentials only from Actions secrets.

**UNVERIFIED:**
- Azure Files SMB locking behavior for SQLite and whether SQLite's locking/transaction model is safe on the selected share.
- Whether `nobrl` is supported or needed, and which mount options Aspire or the planned Bicep deployment should use.
- Rollout behavior with one replica: ACA single-revision mode keeps the old revision active until the new revision passes readiness, so per-revision `maxReplicas: 1` does not prove only one process can access SQLite during rollout. Stop-before-start semantics, if available through the planned deployment path, must be verified; otherwise use an explicit maintenance stop or reconsider SQLite.
- A production backup and restore approach. SQLite online backup or a quiesced copy to separate protected storage are candidates, not a verified procedure.
- Which forwarded headers ACA ingress supplies and which proxies the API must trust for rate limiting.
- Whether all express-only assumptions outside the active architecture references are gone. The active references were updated; append-only history and older decision records still contain historical ACA express wording and were intentionally not rewritten.

**Sources checked:** Aspire 13.6 [ACA deployment](https://aspire.dev/deployment/azure/container-apps/) and [ACA configuration](https://aspire.dev/integrations/cloud/azure/configure-container-apps/); Microsoft Learn [ACA storage mounts](https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts) and [ACA revisions](https://learn.microsoft.com/en-us/azure/container-apps/revisions). Locally inspected the exact cached `Aspire.Hosting.Azure.AppContainers` 13.6.0 package XML/nuspec and its `Azure.Provisioning.AppContainers` 1.2.0 dependency XML for project publish, volume, mount, and environment-storage APIs; ran `dotnet build src\Tilsynsvakt.AppHost --no-restore` against the unchanged AppHost baseline. The local metadata and build do not verify complete generated Azure Files wiring or SQLite safety/behavior in an unprovisioned deployment.

### 2026-10-03: SQLite on Azure Files in ACA - verification
**By:** Fact Checker
**What:** Verified ACA storage and rollout behavior, SQLite network-file cautions, ingress headers, and Azure Files backup choices against current official docs. Recommendation: switch the roster system of record to Azure Table Storage before production; keep SQLite on Azure Files only as a time-limited experiment, not as the chosen persistent store.
**Why:** SQLite depends on correct locking and sync behavior. SQLite explicitly places remote database files at the user's risk, and ACA's documented zero-downtime revision flow can have the old and new revisions alive together. Neither ACA nor Azure Files documents an exclusivity guarantee for this design.

#### 1. Standard ACA Azure Files mounts - Verified; v1 and specific mount options Unverified
Microsoft's ACA storage guide says: "Azure Files supports both SMB (Server Message Block) and NFS (Network File System) protocols." It supports persistent Azure Files volumes and describes `AzureFile` for SMB and `NfsAzureFile` for NFS. Source: https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts

The default Workload profiles v2 environment includes a Consumption profile. Microsoft recommends v2 for new environments. Standard ACA's documented Azure Files configuration therefore covers a workload-profiles environment using its Consumption profile. Whether SMB mounting is supported on the legacy Consumption-only v1 environment is **Unverified**; the docs reviewed do not state it explicitly. Source: https://learn.microsoft.com/en-us/azure/container-apps/structure

ACA supports only classic shares (`Microsoft.Storage/storageAccounts/fileServices/shares`), not the newer top-level `Microsoft.FileShares` resource type. The tutorial's working example uses a StorageV2 account with Standard_LRS and an SMB share; that is an example, not a documented exclusive SKU requirement. Source: https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts-azure-files

The ACA tutorial says: "The storage account key is required" and "Container Apps does not support identity-based access to Azure file shares." Keep the key in ACA's secret/storage configuration, never in source. For NFS, ACA requires a custom VNet and does not support encryption in transit; SMB avoids those NFS-specific constraints. Sources: https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts-azure-files and https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts

ACA accepts a comma-separated `mountOptions` field and links to an AKS mount-options article. That AKS article lists `nobrl`, `cache=strict`, `nosharesock`, and `actimeo`, but it is guidance for AKS CSI storage classes, not proof that ACA's mount implementation honors each option. ACA support for `nobrl` or cache options is **Unverified**. Sources: https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts and https://learn.microsoft.com/en-us/troubleshoot/azure/azure-kubernetes/storage/mountoptions-settings-azure-files

#### 2. ACA express storage - Verified; persistent Azure Files claim Contradicted
Express documents: "Each container can mount up to 10 `EmptyDir` volumes. Other volume types and `subPath` aren't supported." Azure Files is explicitly in the unsupported list. Express also does not support Aspire. The unverified claim that express can hold persistent SQLite is **Contradicted**. Source: https://learn.microsoft.com/en-us/azure/container-apps/express-overview

#### 3. SQLite on SMB/Azure Files - Needs Investigation
SQLite's corruption guide says: "some filesystems contain bugs in their locking logic" and calls this "especially true of network filesystems." Its network-filesystem guide says SQLite relies on exclusive locks for writes, that these have operated incorrectly on some network filesystems, and that rollback mode can mitigate network unreliability only "to an acceptable degree"; SQLite is "not tested in across-a-network scenarios" and remote use is at the user's risk. Sources: https://www.sqlite.org/howtocorrupt.html and https://www.sqlite.org/useovernet.html

WAL is not an option here: SQLite says, "All processes using a database must be on the same host computer; WAL does not work over a network filesystem." `journal_mode=DELETE` is the default rollback journal mode and is preferable to WAL for this design, but does not establish correct SMB locks or sync semantics. Sources: https://www.sqlite.org/wal.html and https://www.sqlite.org/pragma.html

`synchronous=FULL` is prudent and SQLite's default, but do not describe it as a complete rollback-mode power-loss durability guarantee. SQLite's pragma docs say FULL "might also be durable" in rollback mode, depending on the filesystem, and recommend `EXTRA` when rollback-mode power-loss durability is desired. Remote SMB sync behavior remains an unresolved dependency. Source: https://www.sqlite.org/pragma.html

Microsoft.Data.Sqlite documents one pending writer transaction at a time and that operations can time out while another transaction runs. Its docs expose deferred transactions as an explicit `deferred: true` option, but the reviewed page does not explicitly promise the SQL emitted by every `BeginTransaction` overload. Verify the deployed provider/version, or issue the intended `BEGIN IMMEDIATE` explicitly; short transactions and a busy timeout address contention, not broken cross-client locking. Source: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions

Azure Files' Linux SMB docs describe `nobrl` as disabling byte-range lock requests and recommend it only in single-client scenarios where advisory locks are needed. This option is not an ACA-supported-locking guarantee; do not use it to solve multi-replica locking. Test any ACA `mountOptions` on the actual environment. Source: https://learn.microsoft.com/en-us/azure/storage/files/storage-how-to-use-files-linux

Community evidence is not a platform guarantee: a public ACA project issue reports that SQLite on an Azure Files SMB mount stayed in `Activating` while creating tables even with `journal_mode=DELETE`, while the same image started against local disk. This is one user's report, not a controlled general result. Source: https://github.com/nomhiro/news-video-generator/issues/3. A GitHub issues search for Azure Files + SQLite + Microsoft-owned repositories returned no matching issues; the Microsoft Q&A search results were noisy and did not establish a relevant report.

Assessment: a genuinely single client, one writer, rollback journal, correct locks, and correct syncs can reduce the risk, but ACA's replica/revision behavior means this design cannot assume a single client. The exact Azure Files/ACA SQLite locking and `nobrl` behavior remains **Needs Investigation** and must not be treated as supported database semantics.

#### 4. Revision rollout and exclusivity - Overlap Verified; exclusive access Unverified
In single-revision mode, ACA keeps the old revision active until the new revision is ready; readiness includes scaling the new revision to match the previous revision's replica count. The old revision still receives traffic until then. Therefore old and new revisions can overlap and both can mount/open the same Azure Files database. ACA separately documents that multiple containers can mount one Azure Files share across replicas, revisions, and apps. Sources: https://learn.microsoft.com/en-us/azure/container-apps/revisions and https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts

`maxReplicas: 1` is a per-revision target, not a global one-client lock. ACA also says platform upgrades or maintenance can temporarily create more replicas than expected. Multiple-revision mode lets operators control activation/deactivation, but no documented setting guarantees that only one replica/revision has the share open throughout deployment and maintenance. A stop-old-before-start-new runbook can reduce planned-deploy overlap only by accepting downtime; it is not a platform-wide exclusivity guarantee. Sources: https://learn.microsoft.com/en-us/azure/container-apps/scale-app and https://learn.microsoft.com/en-us/azure/container-apps/revisions

#### 5. ACA ingress client IP - Header behavior Verified; trusted proxy ranges Unverified
ACA ingress documents: "If specified in initial request, [the `X-Forwarded-For` header] is appended to. Only the rightmost IP is provided by Azure Container Apps. Any other values must be validated by the user to prevent IP spoofing." The rightmost ACA-provided address is the usable client address for a direct ingress request; do not trust caller-supplied earlier entries. Source: https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview

The networking docs identify the environment's public inbound IP and edge proxy but publish no stable trusted-proxy CIDR list in the pages reviewed. A documented address allow-list is **Unverified**. For ASP.NET per-IP limiting, use a one-hop/`ForwardLimit = 1` policy and verify the exact header chain through ACA ingress; do not parse arbitrary earlier XFF values. Source: https://learn.microsoft.com/en-us/azure/container-apps/networking

#### 6. Azure Files backup - Features Verified; application consistency Unverified
Azure Files snapshots are point-in-time, read-only share copies (up to 200 per share). But Azure Files lists VSS for SMB shares as unsupported; VSS is the feature that would let providers flush data before a snapshot. Therefore no reviewed doc establishes that a share snapshot is SQLite-application-consistent or captures database and rollback journal coherently. Sources: https://learn.microsoft.com/en-us/azure/storage/files/storage-snapshots-files and https://learn.microsoft.com/en-us/azure/storage/files/files-smb-protocol

Azure Backup offers snapshot-tier and vaulted backups for SMB shares. Snapshot-tier recovery points remain in the source storage account; vaulted backup copies changed data to the Recovery Services vault. Azure Backup uses storage-account-key access, so key access must remain enabled. Treat snapshots as a secondary recovery layer, not as a substitute for an SQLite-aware backup or a separate-account copy. Sources: https://learn.microsoft.com/en-us/azure/backup/azure-file-share-backup-overview, https://learn.microsoft.com/en-us/azure/backup/backup-azure-files-faq, and https://learn.microsoft.com/en-us/azure/backup/azure-file-share-support-matrix

SQLite says its Online Backup API and `VACUUM INTO` make consistent backups while the database is live; copying the file directly is safe only when no transaction is active, and a hot journal must remain paired with the database. Microsoft.Data.Sqlite's `BackupDatabase` blocks other connections from writing during the copy, acceptable for this tiny roster. Prefer `BackupDatabase` to local scratch, integrity-check it, then upload to a separate Blob/storage account and test restore. Sources: https://www.sqlite.org/howtocorrupt.html, https://www.sqlite.org/lang_vacuum.html, and https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup

#### Devil's Advocate
**Strongest counter-argument:** SQLite is a local embedded database, not a server process. Its correctness depends on remote filesystem locks and flush ordering that SQLite explicitly warns are variable and untested, while ACA's default deployment intentionally overlaps revisions. Low request volume reduces contention frequency, not the consequence or possibility of a lost roster.

**30-day pre-mortem:** A routine deployment or platform maintenance starts a second ACA replica while the first still has the database open. SMB locking or sync semantics fail to serialize an update; the new revision hangs, returns `SQLITE_BUSY`/`IOERR`, or corrupts the roster. A share snapshot may preserve a point-in-time but not SQLite-aware state, and snapshot-tier backups are in the same storage account. Recovery then depends on a verified application-level backup or manual reconstruction.

**Alternative:** Azure Table Storage, with a shift entity keyed by partition and row key (for example month + date/shift ID), and conditional updates using ETags. Microsoft documents unique `PartitionKey`/`RowKey` pairs, ETag optimistic concurrency, and Microsoft Entra authorization with the `Storage Table Data Contributor` role. It removes the volume, SMB lock, and revision-overlap risks. Table Storage is described as relatively inexpensive, but exact current regional cost was not calculated. Complexity trade-off: moderate repository/data-access rewrite and loss of EF Core relational queries/migrations; reasonable for a few hundred simple roster rows and a handful of daily requests. Sources: https://learn.microsoft.com/en-us/azure/storage/tables/table-storage-design, https://learn.microsoft.com/en-us/rest/api/storageservices/update-entity2, and https://learn.microsoft.com/en-us/azure/storage/tables/authorize-access-azure-active-directory

#### Recommendation: Switch before production
**Status:** Adopted by the user; Azure Table Storage is the selected persistence design (2026-10-03).
Use Azure Table Storage as the roster system of record. Standard ACA can mount Azure Files SMB, but that fact does not make SQLite-on-SMB a supported or reliably exclusive database. The combination of unverified cross-client locking/sync and documented revision/maintenance overlap leaves a correctness risk with no configuration-only mitigation.

**Backend must implement:** a Table Storage repository; stable date/shift keys; conditional ETag writes for signup/change conflicts; managed identity with least-privilege `Storage Table Data Contributor`; API tests for concurrent conflicting updates and retry behavior. Remove SQLite-specific migrations and transaction assumptions as part of that data-layer change.

**Infra must implement:** a classic Azure Table Storage account and table, ACA managed identity plus scoped data role, and no Azure Files database mount. Keep independent backups/export of the roster and verify restore. Do not deploy the current express tier for durable state; express supports only `EmptyDir` and does not support Azure Files or Aspire.

**If SQLite on Azure Files is temporarily retained instead:** use standard ACA on a workload-profiles v2 Consumption environment, SMB classic share, `journal_mode=DELETE`, at least `synchronous=FULL` (consider `EXTRA` for rollback-mode durability), short explicit immediate transactions and a busy timeout; do not enable WAL or `nobrl` as a locking workaround. Treat `minReplicas=maxReplicas=1` as insufficient. Require a tested stop-old-before-start-new downtime runbook, real-mount two-writer/forced-revision tests, `BackupDatabase` to separate storage, and a restore drill. These reduce risk; they do not create a documented ACA exclusivity guarantee.

### 2026-10-03: Switch persistence from SQLite to Azure Table Storage
**By:** user (via Copilot)
**What:** Replace the SQLite persistence layer with Azure Table Storage (standard ACA stays the compute target). Fix all open issues afterwards: failing tests, unmerged inbox, stale docs.
**Why:** User decision after the Fact Checker brief (fact-checker-sqlite-azure-files.md): SQLite over Azure Files SMB has unverified locking/durability, and ACA revisions can overlap so one replica does not guarantee a single database client. Supersedes the SQLite and Azure Files volume parts of lead-api-design.md (sections 2, 7, 8) and copilot-directive-2026-10-03-standard-aca.md.

### 2026-10-03: Azure Table Storage implementation design
**By:** Lead (Leif Bjarte Johansson)
**What:** Replace SQLite/Azure Files with Azure Table Storage while preserving the existing HTTP contract, validation order, error codes, and ProblemDetails bodies. Use one table and one partition, retain the existing `IStores` API seam, and use an in-memory API-test fake plus a separate Azurite-backed storage-contract test target.
**Why:** A single partition lets guard/name-index/counter/shift writes use Azure Table entity-group transactions and ETags for cross-request concurrency. The data volume is tiny, while avoiding a shared SQLite file removes file-lock and ACA revision-overlap correctness risks.

#### 1. Storage seam and tests
Keep `IStores` as the handler-facing interface already in `src/Tilsynsvakt.Api/Stores.cs`; do not edit endpoints, request parsing, validation, DTOs, or `ProblemMiddleware` as part of the storage replacement. Replace the SQLite implementation with `TableStores : IStores`. `TableStores` calls the existing `Normalization` and `Errors` helpers and converts Table Storage concurrency responses into the same `ApiException`s the handlers already expect.

Use this exact `IStores` contract for Backend and Tester. `SignUpResult` remains the existing record:

```csharp
public sealed record SignUpResult(ShiftDto Shift, bool Created);

public interface IStores
{
    Task<IReadOnlyList<GuardDto>> GetActiveGuardsAsync(CancellationToken ct);
    Task<IReadOnlyList<AdminGuardDto>> GetAllGuardsAsync(CancellationToken ct);
    Task<IReadOnlyList<ShiftDto>> GetShiftsAsync(DateOnly from, DateOnly to, ShiftCalendar calendar, CancellationToken ct);
    Task<ShiftDto?> GetShiftAsync(DateOnly date, CancellationToken ct);
    Task<SignUpResult> SignUpAsync(DateOnly date, int guardId, CancellationToken ct);
    Task<ShiftDto> ReplaceAsync(DateOnly date, int guardId, int expectedGuardId, CancellationToken ct);
    Task DeleteAsync(DateOnly date, int? expectedGuardId, CancellationToken ct);
    Task<AdminGuardDto> CreateGuardAsync(string? name, string? phone, CancellationToken ct);
    Task<AdminGuardDto> UpdateGuardAsync(int id, string? name, string? phone, bool? active, CancellationToken ct);
    Task DeactivateGuardAsync(int id, CancellationToken ct);
}

public interface IStoreLifecycle
{
    Task InitializeAsync(CancellationToken ct);
    Task CheckReadyAsync(CancellationToken ct);
}
```

`TableStores` implements both interfaces. The host calls `InitializeAsync` before serving requests; `TableHealthCheck` calls `CheckReadyAsync`. The main `WebApplicationFactory` fixture replaces `IStores` with a lock-protected in-memory fake and `IStoreLifecycle` with a no-op/healthy fake, so it neither constructs a Table client nor needs Azurite, Docker, or a network. Fake operations must implement the same domain outcomes and status codes as `TableStores`.

Choose strategy (a): keep the normal API suite service-free, and add a small opt-in Azurite contract-test class/target against the real `TableStores`. Contract tests exercise table creation/bootstrap, ETags, entity-group transactions, duplicate-key responses, parallel signups, stale replace/cancel, and restart persistence. Keep them outside the default API-test invocation (for example, a separate test project and CI job). Azurite Tables is officially documented as Preview, so also run a minimal real-Azure pre-production smoke test before cutover; emulator success alone is not a cloud guarantee.

Preserve name casing exactly as `Normalization.Name` returns it: trim/collapse spaces and NFC-normalize for `Name`; uppercase invariant NFC form only for `NameKey`. The existing normalization test that expects `"Anna larsen"` from lowercase `"anna larsen"` contradicts this rule; update it to expect `"anna larsen"` and assert its key is `"ANNA LARSEN"`. Do not introduce title-casing.

#### 2. Table and entity layout
Use one classic Azure Storage Table, configured as `Storage:TableName` with default `Tilsynsvakt`, and one fixed `PartitionKey` value `roster` for every entity. This is intentionally a single-partition design: it gives atomic cross-entity writes, and the expected dataset (dozens of guards and about 100 shifts per school year) does not need partition-level write throughput. Separate tables would prevent a single entity-group transaction from atomically updating guard, unique-name index, counter, and shift rows.

Use these `RowKey` forms and typed properties:

| Entity | RowKey | Properties |
|---|---|---|
| Guard | `G_{id:D10}` | `Id` (Int32), `Name`, `NameKey`, `Phone`, `Active` (Boolean), `Version` (Int32) |
| Shift | `S_yyyy-MM-dd` | `Date` (ISO date), `GuardId` (Int32), `GuardName`, `GuardPhone` |
| Name index | `N_{base64url(UTF8(NameKey))}` | `NameKey`, `GuardId` (Int32) |
| Guard ID counter | `META_GUARD_ID` | `NextId` (Int32) |

Base64url-encoding the complete normalized key (not a hash) makes the index row key reversible and collision-free while keeping Azure Table key characters safe; the 80-scalar name limit keeps it comfortably below the 1,024-character row-key limit. The name-index row is unique because `(PartitionKey, RowKey)` is unique. Keep the index for inactive guards too: the current SQLite `NameKey UNIQUE` constraint reserves a name even after deactivation. Sort guard results by `NameKey` in application memory because Table Storage has no secondary index on that property. Query shifts by the `S_` row-key date range and compose open dates with `ShiftCalendar`, as the current store does.

**Guard IDs:** create the counter entity once during initialization with `NextId = 0`; concurrent initialization treats `EntityAlreadyExists` as a harmless race and then reads the winner's counter. For guard creation, read its value and ETag, then submit one transaction: conditional `UpdateReplace` counter to `NextId + 1`, `Add` guard, and `Add` name-index entity. Retry a counter ETag failure after rereading the counter. A duplicate name-index insert fails the entire transaction, so no ID is consumed; map that `409 EntityAlreadyExists` to `Errors.GuardNameTaken()`. IDs are monotonic and never reused; fail explicitly on Int32 exhaustion.

Guard rename uses one transaction containing conditional deletion of the old index, insertion of the new index, and conditional guard replacement. If the key is unchanged, omit both index operations. An index insert conflict maps to `guard_name_taken`; transaction atomicity leaves the old guard and index intact. Guard deactivation/update changes `Version` and uses the guard's observed ETag; do not delete guards or their index rows.

Azure entity-group transactions require one table, a common `PartitionKey`, and no entity repeated within the batch; the service supports at most 100 operations and a payload under 4 MiB. Every transaction proposed here uses at most three operations.

#### 3. Signup, deactivation, replace, and cancel concurrency
**Signup:** first read the requested guard and require `Active == true`; missing or inactive maps to `Errors.UnknownGuard()` (422). Read the shift row. If it is already held by this guard, return `SignUpResult(current, false)` (200); if held by another guard, throw `Errors.ShiftTaken(current)` (409). Otherwise submit one entity-group transaction with (1) a conditional guard update using its observed ETag and incremented `Version`, and (2) `Add` of the date-keyed shift containing the guard ID/name/phone snapshot. This guard write is deliberate: it makes the signup transaction conflict with a concurrent deactivation/update of that guard. If deactivation wins, the stale guard ETag yields 412; reread the guard, return 422 if inactive, otherwise retry using the newest guard snapshot. If another signup wins, the shift insert yields 409; reread the shift and return idempotent 200 for the same guard or `shift_taken` 409 with the current shift for a different guard. If a conflicting row disappeared before reread, retry the bounded read/transaction sequence rather than inventing a conflict.

**Deactivation/update:** use conditional guard updates with the guard ETag and increment `Version`; a transaction racing a signup cannot silently deactivate a guard while accepting a stale signup. If the guard write commits first, signup retries and rejects the inactive guard. If signup commits first, the existing shift snapshot remains valid and is unaffected by the later deactivation. Name-index updates remain in the same transaction as guard updates.

**Replace:** retain the existing order: validate active target guard first, then load shift, then compare `current.Guard.Id` to `expectedGuardId`. An initially missing shift remains 404 `shift_not_taken`; an expected-ID mismatch remains 409 `shift_changed` with the current shift. If target and current IDs already match, return the unchanged `200` only after the endpoint's date checks and active-target validation. Otherwise transact a conditional update of the active target guard (increment `Version`, using its ETag) and a `UpdateReplace` of the shift using its observed ETag; write the replacement's current name/phone snapshot. A target-guard 412 is reread/retried or becomes 422 if now inactive. A shift 412 means the expected occupant is stale: reread and return 409 `shift_changed` with the current shift, or the open representation if the row has disappeared. A missing row discovered before the first compare remains `shift_not_taken` (404); a row disappearing after that read is a concurrency conflict (409).

**Cancel:** preserve the API's existing expected-ID precondition, not a client-visible ETag. An initially open eligible date returns 204, even when `expectedGuardId` is absent. A taken row without it returns 428 `precondition_required`; a different ID returns 409 `shift_changed` with current shift. Delete only with the ETag read from that row. A 412 or a not-found race after the initial read triggers a reread and returns 409 `shift_changed` with current/open state; after a later request observes the row open, return 204. No unconditional (`ETag.All`) shift writes/deletes are allowed.

#### 4. HTTP contract and storage-error translation
Do not expose `RequestFailedException`, Table error names, ETags, or SDK status codes to callers. Map only known persistence conflicts and pass all other storage faults to normal server-error handling. Azure Tables reports duplicate entities as `409 EntityAlreadyExists`, a failed conditional write as `412 UpdateConditionNotSatisfied`, and missing entities as `404 EntityNotFound`; `TableClient.GetEntityIfExistsAsync` normally expresses an absent row through `HasValue == false`. Treat 404 from an absent entity as a business outcome only at the specific read/write points below; a missing table is a readiness/startup fault.

| Existing contract path | Table operation/outcome | API result |
|---|---|---|
| Signup new shift | Guard-ETag update + shift `Add` batch succeeds | 201 with current shift and `Location` |
| Signup repeats same guard | Read active guard, find shift with same ID | 200 unchanged |
| Signup occupied by other guard | Shift `Add` returns 409 `EntityAlreadyExists`; reread occupant | 409 `shift_taken`, `currentShift` populated |
| Signup guard missing/inactive | Guard point-read absent or `Active == false` (including deactivation race) | 422 `unknown_guard` |
| Replace succeeds or is already same occupant | Conditional shift update batch, or validated no-op | 200 shift |
| Replace initially finds no shift | Shift point-read absent | 404 `shift_not_taken` |
| Replace stale expected occupant / shift ETag race | Compare ID mismatch, 412, or row disappears after initial read; reread | 409 `shift_changed`, with current or open `currentShift` |
| Replace target guard missing/inactive | Guard point-read absent/inactive or deactivation wins | 422 `unknown_guard` |
| Cancel finds open shift | Shift point-read absent | 204, even without expected ID |
| Cancel finds taken shift with no expected ID | No write attempted | 428 `precondition_required` |
| Cancel stale expected ID / ETag race | Compare mismatch or conditional delete 412/404 after initial read; reread | 409 `shift_changed`, with current or open `currentShift` |
| Cancel matching expected ID | Conditional ETag delete succeeds | 204 |
| Admin create duplicate name | Name-index `Add` returns 409 and rolls back batch | 409 `guard_name_taken` |
| Admin update duplicate name | New name-index `Add` returns 409 and rolls back batch | 409 `guard_name_taken` |
| Admin update/deactivate absent guard | Guard point-read absent | 404 `guard_not_found` |
| Admin create/update/deactivate success | Corresponding transaction succeeds | 201 / 200 / 204 respectively |

The existing request/date/name/phone validation still yields its current 400, 413, 415, 422, and 429 responses before/around the same store calls; the store replacement must not reorder endpoint validation or change localized titles/details/codes. In particular, a missing `expectedGuardId` is still 428 only when a shift is taken; malformed IDs remain 400; ineligible single-shift GET remains 404 while ineligible mutations remain 422.

#### 5. Client authentication, configuration, and table lifecycle
Add `Azure.Data.Tables` and `Azure.Identity` to the API. Construct a singleton `TableServiceClient`/`TableClient` from configuration:

- Prefer Aspire's `TABLES_CONNECTIONSTRING` when supplied by `WithReference(tables)`, then explicit `Storage:ConnectionString` for direct local/test configuration.
- Otherwise use `Storage:TableServiceUri` (ACA value such as `https://<account>.table.core.windows.net/`) and `new DefaultAzureCredential()` with `new TableServiceClient(uri, credential)`.
- Read the table name from `Storage:TableName` (default `Tilsynsvakt`).
- Local standalone API runs use .NET User Secrets for `Storage:ConnectionString` with Azurite's well-known development connection string. Do not commit any real account key or real connection string. AppHost emulator mode supplies its generated Azurite connection string; Azurite is not a credential secret.

Use a system-assigned ACA managed identity and assign **Storage Table Data Contributor** at the narrowest viable table scope. Provision the table in Bicep before the API starts (`Storage:CreateTable=false` in ACA); startup then seeds the counter entity and fails clearly if table access is unavailable. Locally, `Storage:CreateTable=true` lets `InitializeAsync` call `CreateIfNotExistsAsync` before counter seeding. This avoids granting production the broader storage-account scope that may be required for a create-table operation. Microsoft documentation confirms table-scoped role assignment and the data-contributor read/write/delete role, but the precise RBAC action required for the SDK's create-table call was not independently verified; if production table creation is chosen instead of Bicep pre-creation, verify role scope/actions and startup behavior first.

#### 6. Health and Aspire local flow
Replace `DatabaseHealthCheck` with a readiness check over `IStoreLifecycle.CheckReadyAsync`. Check readiness with a point read of the seeded `META_GUARD_ID` entity. A missing table/counter, network failure, or authorization failure is unhealthy and `/health` returns 503. Keep `/alive` unchanged and process-only; it must not contact Table Storage. Retain the existing `ready`/`live` health tags and explicit endpoint mappings.

Aspire **13.6.0 APIs verified against the current official Aspire documentation**:

```xml
<PackageReference Include="Aspire.Hosting.Azure.Storage" Version="13.6.0" />
```

```csharp
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator();
var tables = storage.AddTables("tables");

builder.AddProject<Projects.Tilsynsvakt_Api>("api")
    .WithReference(tables)
    .WaitFor(tables)
    .WithHttpHealthCheck("/health");
```

`WithReference(tables)` provides `TABLES_CONNECTIONSTRING` in emulator mode; `TABLES_TABLEENDPOINT` is the endpoint property used for token-based cloud access. The API factory above should accept these Aspire names as well as the explicit `Storage:*` keys. Keep the existing `dotnet run --project src/Tilsynsvakt.AppHost` project-run workflow; remove only its SQLite `.data` setup. Starting this AppHost requires Docker because Aspire starts the Azurite container. Direct API development uses the User Secrets connection string and a separately running Azurite instance.

Azurite Table support is officially marked Preview. Its table service exists and is suitable for local contract testing, but tests must not treat preview behavior as proof of Azure production behavior. **Verified sources:** Aspire's [Azure Table Storage hosting integration](https://aspire.dev/integrations/cloud/azure/azure-storage-tables/azure-storage-tables-host/) and [connection guide](https://aspire.dev/integrations/cloud/azure/azure-storage-tables/azure-storage-tables-connect/); Microsoft [Azurite overview](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite).

#### 7. Remove SQLite and ACA file assumptions
Remove `Microsoft.Data.Sqlite`, `Database.cs`, `DatabaseHealthCheck.cs`, embedded `Migrations/` and SQL files, SQLite `PRAGMA`s, `Database:Path`, and the database migration startup call. Remove AppHost `.data` directory creation and SQLite path injection, `.data`/database ignores, and ACA Azure Files volume/share and file secret configuration. Remove the single-replica limit as a correctness requirement; retain a low replica count only as an explicit cost/simplicity setting. Table entity ETags and transactions remain authoritative across replicas and overlapping ACA revisions. Keep deployments backward-compatible if future entity shapes change; revision overlap does not make incompatible schema releases safe.

#### 8. Backup, migration, work split, and open question
**Backup/restore:** The Microsoft Azure Storage data-protection matrix reviewed lists Blob and Data Lake protection features, not Table entity soft delete or Table point-in-time restore. Treat built-in Table soft delete/PITR as **not documented and unavailable for this design**; do not imply Blob soft delete protects live Table rows. For this small roster, implement a nightly application-level JSON export of every entity's keys and application properties (including inactive guards, snapshots, counter, and name indexes); omit server-managed `Timestamp` and `ETag` when importing. Store the export in a private Blob container in a separate storage account. Apply a modest retention period (90 days is a starting recommendation); Blob versioning/soft delete may protect the export object only. Test restore by importing into a newly named empty table in batches of at most 100 entities, verifying guard/index/shift counts and IDs, then switching `Storage:TableName`; avoid destructive in-place restore. **Unverified:** no native Table-specific backup/PITR product or exact export mechanism was confirmed in the documentation checked; verify current Azure offerings before implementation and retain the application export unless a tested managed option is selected. Source: Microsoft [data protection overview](https://learn.microsoft.com/en-us/azure/storage/blobs/data-protection-overview).

**One-time cutover:** Before deleting any SQLite file, determine whether it contains roster data that must be retained. If so, export it once and import guards (including inactive state and existing integer IDs), exact `Name`/`NameKey`, shifts/snapshots, and `NextId = max(Id)` into a fresh Table. Do not dual-write. Validate counts and representative API reads before switching production config. If SQLite only held disposable development/test data, start from an empty Table.

**Parallel work after this note is accepted:**
- **Backend:** own `src/Tilsynsvakt.Api`; implement `TableStores`, the exact `IStores` and `IStoreLifecycle` contracts above, client/config registration, initializer/readiness behavior, Azure SDK error translation, and remove SQLite packages/files/migration wiring. Preserve all HTTP handlers and validation.
- **Tester:** own `tests/`; convert the API fixture to the `IStores` in-memory fake and lifecycle fakes, fix the normalization casing assertion, preserve API behavior tests without external services, and add an opt-in separate Azurite contract-test target against the exact interfaces/semantics above.
- **Infra:** own AppHost, Bicep, docs, and CI; add the verified Aspire 13.6.0 storage host integration and Azurite emulator, provision the Table and ACA managed identity/table-scoped role, configure endpoint/table name/create-table false, remove Azure Files volume and replica correctness assumptions, and wire backup/export plus the Azurite/real-Azure validation jobs.

**Open question:** Does the current SQLite database contain any authoritative roster entries to migrate? This is the only cutover input that cannot be settled from code or Azure documentation; keep the source database until the owner confirms export/import is unnecessary or the migration has been verified.
**Status:** Resolved: the user confirmed there is no deployed data to migrate; no roster migration is required (2026-10-03).

#### 9. Verification references and confidence
- **Verified:** Entity-group transaction same-table/same-partition constraints, no repeated entity, atomicity, 100-operation limit, and 4 MiB limit: [Microsoft transaction documentation](https://learn.microsoft.com/en-us/rest/api/storageservices/performing-entity-group-transactions).
- **Verified:** Table partition/row keys are the primary index and ETags support optimistic concurrency: [Table design](https://learn.microsoft.com/en-us/azure/storage/tables/table-storage-design); SDK `UpdateEntityAsync` documents 412 on ETag mismatch: [Azure.Data.Tables API](https://learn.microsoft.com/en-us/dotnet/api/azure.data.tables.tableclient.updateentityasync?view=azure-dotnet).
- **Verified:** `EntityAlreadyExists` maps to 409, `EntityNotFound` to 404, and `UpdateConditionNotSatisfied` to 412: [Table error codes](https://learn.microsoft.com/en-us/rest/api/storageservices/table-service-error-codes). SDK ETag parameters: [DeleteEntityAsync](https://learn.microsoft.com/en-us/dotnet/api/azure.data.tables.tableclient.deleteentityasync?view=azure-dotnet).
- **Verified:** Microsoft Entra authorization, managed identity support, `Storage Table Data Contributor`, and table-level role scope: [Authorize access](https://learn.microsoft.com/en-us/azure/storage/tables/authorize-access-azure-active-directory) and [assign data roles](https://learn.microsoft.com/en-us/azure/storage/tables/assign-azure-role-data-access).
- **Verified:** Azurite Table service exists but is Preview; AppHost package/API/resource/reference/connection variable names are present in the current Aspire hosting/connection docs linked above.
- **Unverified:** Specific Azurite parity for conditional batches and race behavior; validate in the opt-in contract tests and a pre-production real-Azure smoke test. Exact permissions for creating a table with the selected ACA role scope, and Table-specific native soft-delete/PITR availability, also need a final platform check if those assumptions change.

### 2026-10-03: Use Azure Table Storage for standard ACA persistence
**By:** Infra
**What:** Replace the Azure Files/single-replica deployment assumptions with a pre-created Azure Table Storage table accessed by the ACA system-assigned identity; use Aspire-hosted Azurite Tables for local AppHost development.
**Why:** The accepted storage design uses Table transactions and ETags for concurrency across replicas and overlapping revisions, while avoiding SQLite network-file locking and durability risks.

### 2026-10-03: Azure Table Storage backend implementation
**By:** Backend
**What:** Replaced the API's SQLite store with Azure Table Storage using the approved `IStores` and `IStoreLifecycle` contracts, one `roster` partition, conditional ETags, and entity-group transactions. No roster migration was added because the user confirmed there is no deployed data to migrate.
**Why:** Implement the accepted Table Storage design while retaining the existing HTTP behavior and allowing API tests to replace both store interfaces without constructing a Table client.

**UNVERIFIED:** Azurite was not installed or cached, so the Table operations, emulator parity, and requested signup/replace/cancel/concurrency scenarios were not exercised against a real or emulated Table service. A pre-production Azure smoke test remains required.

#### 2026-10-03: Table Storage contention retry hardening
**By:** Backend
**What:** Increased bounded ETag retry attempts from 10 to 15 and added capped exponential jitter (5–80 ms) before retries in guard ID allocation, signup, replacement, guard update, and deactivation. Exhaustion now returns `storage_busy` (503 ProblemDetails) with `Retry-After: 1`. Name-index `EntityAlreadyExists` still maps immediately to `guard_name_taken` (409); cancel retains its designed one-shot ETag conflict behavior (`shift_changed`).
**Why:** The 20-way Azurite guard-creation race exhausted the prior counter retry budget. ETag-protected transactions remain atomic and prevent lost updates while staggered retries give concurrent contenders time to progress.
**Verification:** `dotnet build src\Tilsynsvakt.Api` succeeded with zero warnings. Default tests: 31 passed, 2 Azurite tests skipped. Azurite-enabled contract suite: 33 passed, 0 failed on each of 3 runs, including concurrent ID uniqueness. Temporary loopback-only container removed; port 10002 confirmed free. A pre-production Azure smoke test remains necessary.

### 2026-10-03: Table storage test results
**By:** Tester (Leif Bjarte Johansson)
**What:** Replaced the SQLite API-test fixture with a lock-protected in-memory implementation of `IStores` and `IStoreLifecycle`; corrected the name-casing expectation and malformed `DateOnly` test URLs; added readiness failure/liveness coverage and opt-in Azurite storage contracts. The default suite passed 31 tests with 2 Azurite tests skipped. The full Azurite-enabled suite passed 32 tests and failed 1.
**Why:** The API suite should run without external storage, while the real `TableStores` implementation needs opt-in emulator coverage for transaction and concurrency semantics.

- The broad Azurite contract passed: 20-way signup race, same-guard idempotency, `shift_taken` with current shift, stale replace/cancel, successful replace/cancel, duplicate normalized names, deactivation racing signup, shift snapshot preservation, and persistence through a new `TableStores` instance.
- Backend defect: `src/Tilsynsvakt.Api/TableStores.cs:294`, exercised by `TableStoreContractTests.Concurrent_guard_id_allocation_returns_unique_ids` (`tests/Tilsynsvakt.Api.Tests/TableStoreContractTests.cs:165`; 20 simultaneous creates at line 155). Expected all 20 creates to complete with unique IDs. Actual: four calls throw `InvalidOperationException: Guard creation could not complete after repeated concurrent changes.` The counter ETag contention exhausts the ten-attempt retry limit (`TableStores.cs:19`). Please revise backend retry/ID allocation behavior; source was not changed by Tester.
- Emulator used Azurite through Docker because the npx process did not bind its service ports. Docker container was stopped and the npx temporary directory removed; port 10002 is no longer listening.

### Open items (as of 2026-10-03)

The following remain unverified or are no longer applicable:

- **UNVERIFIED — Azure-hosted Table smoke test:** Run a pre-production smoke test against Azure Table Storage; Azurite coverage does not establish cloud behavior.
- **UNVERIFIED — shared-key-disabled compatibility:** Verify the API and Aspire configuration with storage-account shared-key access disabled.
- **UNVERIFIED — table-scoped RBAC:** Verify the ACA system-assigned identity's table-scoped role assignment and exact SDK data actions, including startup against a pre-created table.
- **UNVERIFIED — scale-to-zero vs per-replica rate limiting:** Validate rate-limit behavior across ACA scale-to-zero and multiple replicas.
- **UNVERIFIED — backup/restore process validation:** Implement and test the Table export/restore process, including a restore drill.
- **UNVERIFIED — forwarded-header trust behind ACA ingress:** Confirm ACA's forwarded-header chain and trusted-hop configuration before relying on client IP rate limiting.
- **Not applicable:** ACA express storage constraints are irrelevant; standard ACA is the selected deployment target.

### 2026-10-05T09:55:13+02:00: Inbox evidence - backend-aspire-tables

#### 2026-10-03: Use Aspire Table Storage client integration
**By:** user (via Backend)
**What:** Register `Aspire.Azure.Data.Tables` 13.6.0 with connection name `tables`; resolve `TableServiceClient` lazily in `TableStores` and disable only the integration health check.
**Why:** Aspire owns local emulator connection details and Azure endpoint credentials. The API keeps its existing readiness check and table initialization because `AddTables` models the service resource, not creation of an application table.

#### 2026-10-03: Local Aspire readiness and persistence
**By:** Backend (requested by user)
**What:** Declared the API HTTP endpoint and Development environment in the AppHost; verified Azurite data survives an AppHost restart, then removed the throwaway verification table.
**Why:** No API launch profile existed, so the existing `WithHttpHealthCheck` had no endpoint and Development-only table creation was not selected.

#### 2026-10-03: Verification status and follow-up
**By:** Backend (requested by user)
**What:** API and AppHost builds succeeded without warnings; full solution build and API tests are blocked by three direct `TableStores` constructor calls in `TableStoreContractTests`. Live ACA/managed-identity access remains unverified.
**Why:** The requested scope excludes test edits; Tester must register the `tables` client from `ConnectionStrings:tables` and construct `TableStores(IServiceProvider, IConfiguration)`.

### 2026-10-05T09:55:13+02:00: Inbox evidence - backend-spond-sync

#### 2026-10-05T09:55:13+02:00: Fail closed on unverified Spond mutations
**By:** Backend, requested by Leif Bjarte Johansson
**What:** Added the .NET 10 console project, strict environment boundary,
authenticated Spond reads, Oslo calendar and pure planner. Pinned Olen/Spond
v1.2.1 and Spond.API v3.0 verify auth2 login, groups, event filters and generic
event editing, but not guardian-only recipient mutation, subgroup context plus
specific guardians, scheduled invitation JSON, creation or deletion. All runtime
mutations remain blocked. Non-dry-run plans requiring a write exit 1. Dry-run
plans, empty plans and unmatched-guardian skips exit 0. A saturated per-day event
list fails closed because no pagination cursor was established.
**Why:** Never invite child members, other families, or guess a production payload.
Internal EventSpec describes the desired contract, not verified wire JSON.
Runtime recipient/invitation readback remains unknown, so live idempotence is
not established. Standard plus extra three-day reminders are also unverified;
the enum has single 24/48/72-hour options, not proof of combined semantics.
**Verification:** Release project build and full solution build passed.
Credential-free Release run exited 1 naming only missing SPOND_USERNAME.
Existing VSTest suite: 37 passed, 2 opt-in Azurite contracts skipped, 0 failures.
No credentials, live Spond calls, branches, commits, workflow edits or test files.

#### 2026-10-05T13:55:57.6648116+02:00: Cycle2 verified metadata boundary and remaining blocker
**By:** Backend, requested by Leif Bjarte Johansson
**What:** Exhausted pinned public Olen v1.2.1 spond/spond.py,
spond/_event_template.py, examples/{groups,ical,manual_test_functions}.py,
tests/test_spond.py; Spond.API v3.0 Services/SpondClient.cs,
Models/{CommonData.Core.V1,SpondEvent,SpondEventUpdateRequest,SpondEventLocation,SpondMember}.cs,
Enums.cs and Spond.API.Test/Program.cs. Explicit events/_events/event paths,
including mixins variants, return 404 at Olen v1.2.1; directory listings agree.
Newer Olen commit 86df2230bf641b0cc6919970d96dd1106f277170 has no create/delete.
Public recursive tree API returned 403, but raw sources/directories were accessible.
Added verified GET /core/v1/sponds/{id} and POST metadata transport, changing
only heading/description, copying every existing template field with no defaults,
checking exact marker/group/future actual start, and suppressing unchanged POSTs.
Changed metadata with meetupTimestamp/scheduledTime/inviteTime is blocked because
those fields cannot be preserved using the verified template. Recipient/response
fields are never written; executable full-sync mutations remain blocked.
Fixed incorrect marker prefix offset/length, discovered by the credential-free mock.
**Why:** Neither requested pinned source establishes create/delete endpoints,
guardian-only subgroup-scoped recipients, scheduled invitation JSON/readback,
recipient replacement without response reset/reinvites, or combined standard and
extra three-day reminders. Generic update and autoAccept=false are source-confirmed,
not evidence that these missing contracts can be inferred. recipients.group and
response member IDs cannot supply full verified EventSpec. No entire-subgroup or
child invitations, guessed mutation endpoints, credentials or live calls are used.
**Verification:** Narrow Release build passed. In-memory HTTP probe passed:
one metadata POST followed by second-run no-op; protected fields unchanged in the
mock; unowned/wrong-group/past/meetup/scheduled/invite cases blocked. This is NOT
live-tested server notification or response-preservation evidence. Full scheduled
roster-sync runtime idempotence remains unverified. Pilot env limit, location,
public logging and unmatched-existing-event preservation contracts unchanged.

**Final validation:** dotnet build Tilsynsvakt.slnx passed;
dotnet test Tilsynsvakt.slnx --no-build: 37 passed, 2 opt-in Azurite tests skipped,
0 failures. Test project discovery found only the API VSTest/xUnit project;
no additional sync test project was available at the final check. No test files
were changed. Editor diagnostics for the three changed sync C# files are clear.

#### 2026-10-05T13:55:57.6648116+02:00: Targeted pinned directory evidence completion
**By:** Backend, requested by Leif Bjarte Johansson
**What:** Replaced guessed-path absence evidence in README with complete actual
Olen v1.2.1 package/examples/tests and Spond.API v3.0 services/models/console
directory inventories. Inspected every Olen package/example/test Python source
except the configuration sample; inspected Spond.API service, both endpoint
builders, all event models, group/member models, enum and console example.
Actual event methods are get_events/get_event/update_event/get_event_attendance_xlsx/
change_response and GetEvents/GetEvent/UpdateEvent/GetEventAttendance/ChangeResponse.
No listed package mixins or additional service files exist. Both pinned clients
lack event creation/deletion implementations; the update payload excludes
recipients, meetup and invitation scheduling fields. Directory inspection is
complete for the recorded paths; unrelated models were inventoried, not claimed
as source-reviewed. Public recursive tree requests were rate-limited through
fetch and unauthenticated PowerShell; directory/raw retrieval succeeded.
**Why:** Endpoint verification must not be confused with safe guardian invitation
payload evidence. No additional requirement-compatible transport was established;
keep existing limited metadata transport and fail-closed executable unchanged.
Absence applies to these pinned clients, not to Spond's private API capabilities.
**Verification:** Narrow Debug sync build and full solution build succeeded.
Unchanged solution tests: 37 passed, 2 opt-in Azurite contracts skipped, 0 failed;
no concurrent sync test project present. Rerun credential-free HTTP mock: two
fresh GETs, one metadata POST, second-run no-op, protected mock fields unchanged,
and unowned/wrong-group/past-start/meetupTimestamp/scheduledTime/inviteTime all
blocked with zero POSTs. This pass edited only README and backend history/inbox;
existing code and all unrelated changes were preserved. Live transport behavior
and full scheduled roster-sync idempotence remain unverified.

### 2026-10-05T09:55:13+02:00: Inbox evidence - copilot-directive-2026-10-03-frontend-scope

#### 2026-10-03: User directive (frontend v1 scope)
**By:** Leif Bjarte Johansson (via Copilot)
**What:**
- Stack: vanilla HTML/CSS/JS with ES modules, no build step.
- Location: `web/` at the repo root.
- v1 features: Vaktliste (roster + my shifts + next handover), Påmelding (sign up / change / cancel via the API), Sjekkliste (checklist progress for today's shift in `localStorage`). Name selection (stored in `localStorage`) is required by these features.
- Deferred: Dagens plan, Kontaktliste, Avviksskjema.
- Serve the frontend from the Aspire AppHost for local dev with the API URL injected.
- Include a GitHub Pages deploy workflow now.
**Why:** User decision — captured for team memory

### 2026-10-05T09:55:13+02:00: Inbox evidence - copilot-directive-2026-10-05-spond-sync

#### 2026-10-05T09:55:13+02:00: User directive (Spond sync requirements)
**By:** Leif Bjarte Johansson (via Copilot)
**What:**
- **Auth:** Leif's personal Spond account (email + password, 2FA off). User explicitly accepts the risk (full-account access if the secret leaks; Spond ToS §5 does not explicitly allow unofficial automation). Secrets `SPOND_USERNAME`, `SPOND_PASSWORD` in GitHub Actions Secrets only; the user sets them, never the team.
- **Runtime:** .NET console app (Spond.API v3.0 or raw HTTP). Login endpoint is `POST https://api.spond.com/core/v1/auth2/login` (old `/login` broke May 2026).
- **Schedule:** every 15 min, 06:00–23:00 Europe/Oslo (cron is UTC), plus `workflow_dispatch` with a `dry-run` input. First runs use dry-run.
- **Window:** today through the end of the current period (28.11 or 29.05).
- **Scope:** only taken shifts get Spond events; open shifts get none. Past events are not touched.
- **Changes:** guard changed → update invitees; shift cancelled → delete event.
- **Ownership:** the sync only touches events containing a hidden marker line in the description, e.g. `[tilsynsvakt-sync:2026-10-06]`.
- **Event:** title «Tilsynsvakt Tasta Skole»; meet 16:40, start 16:45, end 22:00 (Europe/Oslo); location «Tasta Skole, Randabergveien»; description «Se nettside https://tasta-skolekorps.github.io/tilsynsvakt/ for informasjon, evt. ta kontakt på 924 23 946 hvis spørsmål. God vakt!» + marker line.
- **Responses:** guards are invited (must confirm), not auto-accepted. Reminders: Spond's standard automatic reminder to unanswered invites + an extra reminder 3 days before.
- **Recipients:** the Tilsynsvakt subgroup members are children (band members); parents are guardians on the child's profile. The backend guard (named after the child's last name) has one parent's phone. Match that phone (E.164) to a guardian → invite that parent and the other guardian of the same child. No match → warn, skip that shift, and keep going.
- **Logging:** public logs show only date + action + backend guard name. Never log Spond member/guardian names, IDs or phone numbers.
- **Failures:** GitHub's default failure email (no issue automation).
**Why:** User interview, 2026-10-05.

**Amendments (same day, supersede conflicting points above):**
- **Group:** every event is created in group «Tasta Skolekorps - Medlemmer», subgroup «Tilsynsvakt» (the event's recipients point at that subgroup).
- **Recipients:** invite **only the guardians** (parents) of the matched child, not the child member. The Spond web UI supports this (Invite → Members / Guardians / Subgroups / Contact).
- **Pilot scope:** for now the Action creates at most **one** event: the last shift before the Christmas holiday. With the current calendar (autumn period 01.09–28.11; 28.11.2026 is a Saturday), that is **Thursday 26.11.2026**. Configure this as a single-date limit (not a hard-coded constant) so it can be widened later. If that shift is open, nothing is created.
- **Invitation time:** invitations are scheduled to be sent **7 days before the event start** (Spond's "scheduled event" invite time), e.g. 19.11.2026 16:45 for the 26.11 event. Reminder 3 days before still applies.
- Reference: a manually created event in Spond (Tue 13.10, meet 16:40, 16:45–22:00, «Tasta skole, Randabergveien, Stavanger», host Leif, Tasta Skolekorps - Medlemmer • Tilsynsvakt, scheduled invite) shows the target shape.

### 2026-10-05T09:55:13+02:00: Inbox evidence - infra-spond-sync-workflow

#### 2026-10-05T09:55:13+02:00: Fail-safe Spond workflow contract
**By:** Infra (requested by Leif Bjarte Johansson; issue #39)
**What:**
- `.github/workflows/spond-sync.yml` runs `dotnet run --project tools/Tilsynsvakt.SpondSync -c Release` using `actions/checkout@v4`, `actions/setup-dotnet@v4`, and SDK `10.0.x`, matching Squad CI.
- UTC cron `*/15 4-21 * * *` covers both Oslo offsets. The first step checks the actual `Europe/Oslo` hour and permits scheduled runs only from 06 through 22 inclusive. All remaining steps skip outside that interval; manual dispatch bypasses the hour gate.
- Manual boolean `dry_run` defaults true and preserves explicit false. Scheduled writes require the exact case-sensitive repository variable value `SPOND_SYNC_DRY_RUN` of `false`; all other values select dry-run. Both are injected through environment variables and compared in Bash, never interpolated into script source.
- The app receives `API_BASE_URL` from the repository variable; Spond credentials only from the `SPOND_USERNAME` and `SPOND_PASSWORD` Actions secrets. Secrets are scoped to the presence-validation and sync steps, not checkout/setup. Missing required configuration fails before checkout without logging values.
- The app receives group `Tasta Skolekorps - Medlemmer`, subgroup `Tilsynsvakt`, and `SPOND_SYNC_DATES` from the repository variable with fallback `2026-11-26`. The app owns enforcing the configurable date allowlist and amended event/guardian behavior.
- Fixed concurrency group `spond-sync`, no cancellation of in-progress runs, ten-minute job timeout, and only `contents: read`. Failures propagate normally; no issue or custom notification automation.
**Why:** First runs must be non-mutating and restricted to one pilot date, with no overlapping sync jobs, credential disclosure, or seasonal schedule drift.
**Validation:** A duplicate-key-rejecting PyYAML parser and structural contract assertions passed, as did Bash syntax checks, 44 winter/summer/DST/hour-boundary cases, 20 exact dry-run/injection/manual-bypass cases, and four synthetic presence-validation cases. Git Bash lacks IANA timezone data, so date-based behavior tests used the equivalent POSIX rule `CET-1CEST,M3.5.0/2,M10.5.0/3`; the Ubuntu `Europe/Oslo` lookup is unverified locally. Actionlint is unavailable. No live Spond/backend requests, app builds, or app-file changes were made. Owner must configure secrets/variables and default GitHub failed-run email notifications. GitHub schedules can be delayed; exact fifteen-minute delivery is not guaranteed.

### 2026-10-05T09:55:13+02:00: Inbox evidence - tester-spond-sync

#### 2026-10-05T09:55:13+02:00: Issue #39 pure sync regression coverage
**By:** Tester, requested by Leif Bjarte Johansson
**Scope:** Local state; branch squad/39-spond-sync. Test project, solution registration,
own history and this inbox only. No production edits, workflow/API/frontend/README
edits, prohibited git actions, external service calls, secrets or data/*.json reads.
Environment options use an injected function returning inert test-placeholder values;
they never inspect the process environment. Fixtures use Familien Test / +4790000001
and synthetic test-prefixed identifiers only.

**Local hypothesis:** An exact ownership marker is recognized, and an already matching
verified guardian-only EventSpec produces an empty plan. The first focused executable
check passed both marker round-trip and unchanged-event idempotence tests.

**Coverage:** 88 xUnit cases cover taken-only creation; explicit-open owned deletion;
guardian-change updates; verified-state idempotence and guardian order; unverified-state
update proposals; unmarked and past events untouched; unmatched skips preserving existing
events and continuing later actions; guardian-only subgroup membership and ambiguous,
duplicate, child-ID and missing-ID refusal; three supported phone formats; invalid phones;
exact marker parsing and duplicate-marker refusal; duplicate/inconsistent/missing roster
fail-closed behavior; configurable 26.11 pilot with taken/open cases, excluded existing
event, full-window fallback, date widening and window intersections; DRY_RUN defaults and
validation; configuration error sanitation; exact title/location/description and
autoAccept=false; 16:40/16:45/22:00 Oslo times on 20.10 CEST and 26.11 CET; 19.11 16:45
invitation for 26.11 plus the 27.10 DST-crossing invitation; Oslo today and season edges;
JSON pure helpers, in-memory response sanitation and PublicName workflow-line escaping.

**Commands and measured results:**
- SDK: `dotnet --version` -> 10.0.301; xUnit 2.9.3, VSTest adapter 3.1.5,
  Microsoft.NET.Test.Sdk 17.14.1 match existing API test versions.
- VS Code runTests for SyncPlannerTests.cs initially returned no tests discovered;
  direct VSTest invocation was used thereafter.
- `dotnet test tests/Tilsynsvakt.SpondSync.Tests/Tilsynsvakt.SpondSync.Tests.csproj --filter "FullyQualifiedName~OwnedDate_RoundTripsMarkerOnItsOwnDescriptionLine|FullyQualifiedName~Plan_UnchangedVerifiedEvent_IsIdempotent"`
  -> 2 passed / 0 failed / 0 skipped, immediately after first edit.
- First expanded scoped build failed CS1022 due to an extra brace in the authored test
  file; removed the brace only, then reran the same command. No assertions weakened.
- `dotnet test tests/Tilsynsvakt.SpondSync.Tests/Tilsynsvakt.SpondSync.Tests.csproj`
  -> final 88 passed / 0 failed / 0 skipped (earlier expanded suite 87/0/0).
- `dotnet test Tilsynsvakt.slnx` -> final 127 total: 125 passed / 0 failed / 2 skipped.
  API project: 37 passed / 0 failed / 2 skipped; sync project: 88/0/0.
  Earlier solution run before the final continuation case: 124/0/2.
  Both skips are existing opt-in TableStoreContractTests: atomicity/lifecycle and
  concurrent guard-ID allocation; TILSYNSVAKT_AZURITE=1 was not set or enabled.
  Expected unhealthy in-memory health-check logging did not represent a failed test.

**Failures/bugs:** No production requirement failures found in the exercised slice.
The one test-authoring syntax failure was corrected and is not a production defect.
No tests skipped to conceal failures. No mutation payload inferred or production fix made.

**Concrete gaps:**
- GetEventsAsync supplies VerifiedState=null. Pure idempotence uses a constructed verified
  EventSpec; full live recipient/invitation readback and second-run idempotence are unknown.
- Program's warning label/prefix, complete output assembly, generic catch and exit policy
  remain inline with authenticated orchestration. Pure tests verify SkipUnmatched,
  continuation, sanitized response errors and PublicName, not actual warning emission
  or end-to-end exit codes. No new production extraction was authorized or performed.
- Guardian has no name field, so arbitrary guardian-name suppression at the complete
  output boundary is not exercised. PublicName receives only backend name; tests assert
  synthetic guardian/child/group/subgroup IDs and phones never enter that helper output.
  Reflection depends on compiler-generated PublicName naming, not entry-point execution.
- Limited metadata HTTP transport and preserved response/notification state were not
  exercised by this pure-only suite. No live verification is claimed.
- Create/delete endpoints, guardian-only subgroup wire selection, scheduled invite payloads,
  recipient replacement without response resets/reinvites, and combined standard plus
  extra three-day reminders remain unverified. EventSpec lacks a reminder contract;
  no reminder assertion can establish delivery. Runtime full writes intentionally fail closed.

**Changed paths for coordinator validation:**
- Tilsynsvakt.slnx
- tests/Tilsynsvakt.SpondSync.Tests/Tilsynsvakt.SpondSync.Tests.csproj
- tests/Tilsynsvakt.SpondSync.Tests/SyncPlannerTests.cs
- tests/Tilsynsvakt.SpondSync.Tests/SyncOptionsTests.cs
- tests/Tilsynsvakt.SpondSync.Tests/SyncCalendarTests.cs
- tests/Tilsynsvakt.SpondSync.Tests/SafeHelpersTests.cs
- .squad/agents/tester/history.md (append only)
- .squad/decisions/inbox/tester-spond-sync.md

**Decision:** Tests validate pure pilot safety, scheduling and deterministic planning,
not live production readiness. Keep source-verification and runtime mutation blockers visible.

### 2026-10-05T15:30:00+02:00: User-verified Spond schema supersedes earlier blocker

#### 2026-10-05T15:30:00+02:00: User-verified Spond schema supersedes earlier blocker

**By:** Backend; issue #39.
**Evidence:** User supplied observed schema with writes intercepted/aborted. This
implementation did not repeat that session, execute the app, or call real services.

- GET `/core/v1/sponds` uses `includeComments=true`, `includeHidden=false`,
  `addProfileInfo=true`, `scheduled=true`, `order=asc`, `max=20`, `minEndTimestamp`.
  Event fields include marker description, start/end/meetup timestamps, numeric
  meetupPrior, location id/feature, recipients group/subGroups/members, profiles,
  guardians, inviteTime, visibility, reminder and owner fields. No invented page
  cursor: inclusive end-time overlap, deduplication, stalled-boundary refusal.
- GET `/core/v1/groups`: member.subGroups contains ID STRINGS. Guardian has id,
  phoneNumber, email and optional profile.id; guardian.id is not profileId.
  Match backend E164 phone across all matching children in the subgroup; dedupe
  profile IDs. A required missing profile warns and skips the whole shift.
- POST `/core/v1/sponds`: heading/description, spondType `event`, type `EVENT`,
  start/end UTC, commentsDisabled true, meetupPrior STRING `5`, maxAccepted 0,
  rsvpDate null, location, owners [{id}], visibility INVITEES, participantsHidden
  true, autoReminderType REMIND_48H_BEFORE, matchInfo null, autoAccept false,
  attachments [], tasks {openTasks:[],assignedTasks:[]}, requested inviteTime,
  recipients {groupMembers:[],guardians:[{email,phoneNumber,profileId}],
  group:{id,subGroups:[ID STRING]}}. Owner uses only GET `/core/v1/profile.id`.
- POST `/core/v1/sponds/{id}` metadata: spondType EVENT, heading/description,
  start/end, commentsDisabled, rsvpDate null, inviteTime, location {id,feature},
  owners [{id}], visibility, participantsHidden, autoReminderType, matchInfo null,
  autoAccept, payment, attachments, id, tasks. NO recipients or response writes.
- POST `/core/v1/sponds/{id}/recipients`: profiles [], group {id,subGroups:[{id}]},
  groupMembers [], guardians [{email,phoneNumber,profileId}]. Adds only; pure
  builder retained but not used for replacements. No verified removal contract.
- DELETE `/core/v1/sponds/{id}?quiet=true`: silent deletion. Changed guardians
  produce ordered quiet DELETE then CREATE; explicit open shifts delete only owned
  future events. Cancel-and-notify remains unverified. Replacement is not atomic.
- Exact title/description and date marker retained. Start 16:45/end 22:00 Oslo;
  invite seven Oslo wall-clock days earlier, including DST crossing. DRY_RUN defaults
  true; explicit false no longer hits the obsolete global refuse-writes guard.
- Local tests cover fake wire payloads, profile matching, missing-profile skip,
  replacement order, dry-run zero requests, metadata no-op, paging and refusal.
  Logs expose only date, action and backend guard name; no Spond data is logged.

**Unverified/open:** feature-only location without id; inviteTime on CREATE (only
event/update usage observed); additional three-day reminder. Only REMIND_48H_BEFORE
is verified. Event guardian item fields were not provided: local readback supports
explicit profileId and fails closed otherwise; live full-sync idempotence is not
claimed. Notification/response preservation and server acceptance remain untested.

### 2026-10-09T09:30:00+02:00: User directive (Spond sync scope)
**By:** Leif Bjarte Johansson (via Copilot)
**What:** Remove the one-event pilot limit. The workflow no longer falls back to `2026-11-26`; the sync covers today through the end of the current period. `SPOND_SYNC_DATES` remains an optional repository variable to limit dates. Supersedes the pilot-scope points in earlier Spond entries.
**Why:** Pilot event on 26.11 was created and verified (issue #39 follow-up, PR #42).

### 2026-10-05T15:45:00+02:00: User directive (Spond reminder and location)
**By:** Leif Bjarte Johansson (via Copilot)
**What:** Accept `REMIND_48H_BEFORE` as the only automatic reminder; the extra 3-day reminder is dropped (Spond's UI does not offer it). Accept location feature text «Tasta skole, Randabergveien, Stavanger» without location id; the first non-dry run confirms it.
**Why:** User decision, issue #39.

### 2026-10-05T15:30:00+02:00: Fail-safe Spond workflow contract after project relocation

#### 2026-10-05T09:55:13+02:00: Fail-safe Spond workflow contract
**By:** Infra (issue #39)
**What:**
- `.github/workflows/spond-sync.yml` runs `dotnet run --project src/Tilsynsvakt.SpondSync -c Release` using `actions/checkout@v4`, `actions/setup-dotnet@v4`, and SDK `10.0.x`, matching Squad CI.
- UTC cron `*/15 4-21 * * *` covers both Oslo offsets. The first step checks the actual `Europe/Oslo` hour and permits scheduled runs only from 06 through 22 inclusive. All remaining steps skip outside that interval; manual dispatch bypasses the hour gate.
- Manual boolean `dry_run` defaults true and preserves explicit false. Scheduled writes require the exact case-sensitive repository variable value `SPOND_SYNC_DRY_RUN` of `false`; all other values select dry-run. Both are injected through environment variables and compared in Bash, never interpolated into script source.
- The app receives `API_BASE_URL` from the repository variable; Spond credentials only from the `SPOND_USERNAME` and `SPOND_PASSWORD` Actions secrets. Secrets are scoped to the presence-validation and sync steps, not checkout/setup. Missing required configuration fails before checkout without logging values.
- The app receives group `Tasta Skolekorps - Medlemmer`, subgroup `Tilsynsvakt`, and `SPOND_SYNC_DATES` from the repository variable with fallback `2026-11-26`. The app owns enforcing the configurable date allowlist and amended event/guardian behavior.
- Fixed concurrency group `spond-sync`, no cancellation of in-progress runs, ten-minute job timeout, and only `contents: read`. Failures propagate normally; no issue or custom notification automation.
**Why:** First runs must be non-mutating and restricted to one pilot date, with no overlapping sync jobs, credential disclosure, or seasonal schedule drift.
**Validation:** A duplicate-key-rejecting PyYAML parser and structural contract assertions passed, as did Bash syntax checks, 44 winter/summer/DST/hour-boundary cases, 20 exact dry-run/injection/manual-bypass cases, and four synthetic presence-validation cases. Git Bash lacks IANA timezone data, so date-based behavior tests used the equivalent POSIX rule `CET-1CEST,M3.5.0/2,M10.5.0/3`; the Ubuntu `Europe/Oslo` lookup is unverified locally. Actionlint is unavailable. No live Spond/backend requests, app builds, or app-file changes were made. Owner must configure secrets/variables and default GitHub failed-run email notifications. GitHub schedules can be delayed; exact fifteen-minute delivery is not guaranteed.
