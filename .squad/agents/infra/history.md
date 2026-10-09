# Infrastructure History

- **Owner:** Unavailable (git config user.name could not be read)
- **Project:** Tilsynsvakt guard handbook and roster
- **Stack:** GitHub Actions, GitHub Pages, .NET minimal API with Aspire, Azure Container Apps (express)
- **Created:** 2026-10-03T09:07:58.155Z

## Learnings

- The static frontend is hosted on GitHub Pages; the .NET minimal API is orchestrated with Aspire and deployed to Azure Container Apps (express).
- Keep credentials in GitHub Actions Secrets or the Azure Container Apps secret store, never in repository or frontend files.
- A scheduled GitHub Action syncs the backend roster one-way to Spond. Neither the browser nor backend API calls Spond.
- Backend persistence is undecided and is not an infrastructure-owned decision. Preserve CORS restrictions to the frontend origin.
- GitHub Pages deployment must replace `web/config.js` from the public `API_BASE_URL` repository variable; use an environment variable and JSON encoding to write the value safely.
- Current official Pages action major tags verified for this workflow: checkout v7, configure-pages v6, upload-pages-artifact v5, deploy-pages v5. Existing workflows use major-only action tags.
- The Pages API returned 404 and the repository `API_BASE_URL` variable lookup returned 404 when checked for issue #3.
- Aspire ACA 13.6 maps supported container bind mounts to Azure Files, but `WithBindMount` only accepts `ContainerResource`; the current API is a `ProjectResource`, so its `/data` volume needs separate Bicep/custom ACA wiring.

## Corrections

- **2026-10-03:** Withdraw the prior claim that `WithBindMount` requiring `ContainerResource` means a `ProjectResource` cannot use AppHost-only ACA publishing or mounting. The exact cached Aspire.Hosting.Azure.AppContainers 13.6.0 metadata documents `PublishAsAzureContainerApp` for project resources with an Azure infrastructure/`ContainerApp` customization callback, and the pinned Azure.Provisioning.AppContainers 1.2.0 metadata exposes ACA volume, mount, and managed-environment-storage models. The complete supported configuration wiring an Azure Files share to this project's published resource remains unverified, so Bicep is recommended as the conservative verified path, not because the project resource is incapable. An unchanged AppHost baseline build passed; no package or AppHost source changes, publish, or Azure activity were performed.
- **2026-10-03:** Aspire 13.6's Azure Storage host integration provides local Azurite Tables and injects `TABLES_CONNECTIONSTRING`; standard ACA should use the system-assigned identity and a pre-created table instead of Azure Files or a single-replica correctness constraint.

📌 Team update (2026-10-03T22:48:50.1676592+02:00): Azure Table Storage is the selected roster persistence for standard ACA; remove SQLite/Azure Files volume and single-replica correctness assumptions. The ACA express target is no longer relevant.

- **2026-10-05T09:55:13+02:00 (issue #39):** Added the scheduled Spond workflow with UTC cron `*/15 4-21 * * *`, Oslo local-hour gate, manual bypass, single-date pilot fallback, narrowly scoped secrets, and exact Bash `false` comparison for fail-safe dry-run. Local PyYAML/Bash checks passed 44 time-boundary cases, 20 dry-run cases, and four presence checks. Windows Git Bash lacks IANA tzdata and `/dev/stdout`; local tests used the equivalent Oslo POSIX timezone rule and captured echo output through stderr with `/dev/null` as the output sink. Actionlint, Ubuntu IANA lookup, and live integration remain unverified; no app or network calls were made.

- Team update (2026-10-05T09:55:13+02:00; issue #39; Scribe for Leif Bjarte Johansson): Leif accepted personal Spond account exposure and ToS section 5 risk after declining a dedicated account; only the owner configures SPOND_USERNAME/SPOND_PASSWORD Actions secrets. Keep dry-run default, configurable 26.11.2026 pilot, Tilsynsvakt subgroup, guardian-only invitations and seven-day invitation scheduling. Workflow dispatch cannot bypass the executable's safety-blocked full-sync writes. Tester reports 125 passed / 0 failed / 2 skipped overall; no live integration or Scribe rerun is claimed.

- Team update (2026-10-05T15:30:00+02:00; issue #39; Scribe): Canonical workflow amendment now names src/Tilsynsvakt.SpondSync and preserves the older tools/ record. Supplied manifest reports implemented create/delete writes, replacing the earlier blanket mutation blocker; dry-run default and existing pilot gates remain relevant. Supplied browser session reports aborting all non-GET requests during observation and removing the route/capture files afterward; this is not a live write acceptance check. Scribe did not run the workflow or tests.

- **2026-10-09T10:40:00+02:00 (issue #44):** Moved ADMIN_API_KEY/USERNAME/PASSWORD from `vars.*` to `secrets.*` scoped to the Deploy step with a fail-early presence check; AppHost now uses `AddParameterFromConfiguration(..., secret: true)` so Aspire publishes them as `@secure()` Bicep params and ACA `secretRef`s (previously plain-text env values in the ACA template). Verified by YAML parse, AppHost build and a dummy-value `aspire publish` (no values in output, secretRef present); actionlint absent, no Azure deploy run.
