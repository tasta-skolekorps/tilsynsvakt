# Standard Azure Container Apps Deployment Plan

## Runtime and persistence

Deploy the .NET API to standard Azure Container Apps. Azure Table Storage is the roster system of record; SQLite, EF Core, Azure Files, and a single-replica correctness requirement are not part of this design. Table transactions and ETags provide the storage concurrency boundary across replicas and overlapping revisions.

Provision the storage account with:

- Anonymous blob access disabled (`allowBlobPublicAccess=false`). This does not by itself disable the storage service endpoint needed by the API.
- Minimum TLS 1.2 and HTTPS-only storage traffic.
- Shared-key access disabled if compatible with the selected Table SDK and all required data-plane operations. Compatibility with the final ACA identity/RBAC configuration is **UNVERIFIED** until smoke-tested.
- The `Tilsynsvakt` table created before the API starts. Production must not rely on the API identity having table-creation permissions.

Enable a system-assigned managed identity on the Container App. Assign **Storage Table Data Contributor** at the narrowest viable scope: the roster table. The table-scoped assignment and the API's actual read/write/transaction behavior must be confirmed during deployment validation; never broaden to account keys as a workaround.

Configure the container with these exact environment-variable names:

| Name | Value/source |
|------|--------------|
| `Storage__TableServiceUri` | `https://<account>.table.core.windows.net/` for the selected account |
| `Storage__TableName` | `Tilsynsvakt` |
| `Storage__CreateTable` | `false` in ACA; create the table before rollout |
| `Admin__ApiKey` | ACA secret reference; never store the key in repository files |
| `Frontend__Origin` | Exact GitHub Pages origin, without a path |

The API obtains table data-plane access through the ACA system identity, not a storage connection string. For local Aspire development, the AppHost's `tables` reference supplies `TABLES_CONNECTIONSTRING` from Azurite; direct API runs use .NET User Secrets for `Storage:ConnectionString` with the Azurite development connection. Do not commit that local setting or put a real account key in AppHost settings, source, or frontend files.

## Ingress and health

Use external HTTPS ingress for the public API and disable insecure HTTP ingress. Keep the API's CORS allow-list restricted to the exact `Frontend__Origin` value. Preserve the trust-based product model; this deployment plan does not add user authentication.

- Liveness probe: `GET /alive`; process-only, with no Table Storage dependency.
- Readiness probe: `GET /health`; checks Table Storage readiness and returns unhealthy when the table or required seed entity is unavailable.

## Scaling

Multiple replicas are storage-correct; do not pin the app to one replica for SQLite file safety. For the small, low-traffic roster, start with minimum one replica for predictable first-request latency and allow a conservatively bounded maximum based on observed demand. Scale-to-zero is a valid cost option if cold-start latency is acceptable and the HTTP scaling rule is verified for the ingress configuration.

The API's in-memory rate limit is per replica, not a global quota. With 1-N replicas, the aggregate permitted request rate can grow with replica count. Before increasing the maximum, either accept and document that behavior or move rate limiting to a shared/global enforcement point; do not describe the per-replica limit as a global cap.

## Backup and restore outline

Plan a nightly application-level JSON export of all Table entities, including partition/row keys and application properties for inactive guards, name indexes, shifts, and the ID counter. Omit server-managed timestamps and ETags when importing. Store exports in a private Blob container in a separate storage account, with anonymous access disabled and a narrowly scoped identity for the backup process. A 90-day retention period is an initial recommendation. Blob versioning or soft delete protects the export object only; it does not protect live Table rows.

Restore into a newly named empty table, importing in batches of at most 100 entities. Verify entity counts, guard IDs, indexes, and representative shifts before changing `Storage__TableName`; avoid destructive in-place restore. Native Table-specific point-in-time restore or soft-delete availability for this design is **UNVERIFIED**. This is an outline only: no workflow, export implementation, or Azure resource is created here.

## Infrastructure-as-code decision

Keep Bicep as an outline for now. The repository has no existing Bicep deployment surface, and a partial skeleton would not establish the target Container App/environment, naming inputs, network boundaries, or release contract. Add a complete, locally build-validated template when those deployment inputs are selected; it should provision the storage account/table, Container App identity and table-scoped role, secret references, ingress, probes, and backup resources together. No Bicep compiler check or Azure operation was run for this documentation-only decision.

## Unverified platform behavior and rollout checks

- Shared-key-disabled behavior with the selected Table SDK and system-assigned ACA identity: **UNVERIFIED**; verify reads, writes, and entity-group transactions in a pre-production ACA smoke test.
- Table-scoped role assignment and transaction authorization at the selected scope: **UNVERIFIED** for the final resource configuration; test the actual identity rather than inferring from a successful local Azurite run.
- Scale-to-zero wake-up and readiness behavior for the chosen ACA HTTP scaling rule: **UNVERIFIED**; measure cold-start latency before selecting zero minimum replicas.
- Table-specific native backup/PITR: **UNVERIFIED**; retain and test the application export until a supported managed option is demonstrated.

Before cutover, determine whether the existing SQLite database contains roster data that must be migrated. Keep the source intact until export/import and representative API reads are verified. Azurite Table support is Preview, so emulator success is not a substitute for the pre-production Azure smoke test.