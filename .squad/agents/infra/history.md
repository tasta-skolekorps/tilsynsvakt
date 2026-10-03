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
- Aspire ACA 13.6 maps supported container bind mounts to Azure Files, but `WithBindMount` only accepts `ContainerResource`; the current API is a `ProjectResource`, so its `/data` volume needs separate Bicep/custom ACA wiring.

## Corrections

- **2026-10-03:** Withdraw the prior claim that `WithBindMount` requiring `ContainerResource` means a `ProjectResource` cannot use AppHost-only ACA publishing or mounting. The exact cached Aspire.Hosting.Azure.AppContainers 13.6.0 metadata documents `PublishAsAzureContainerApp` for project resources with an Azure infrastructure/`ContainerApp` customization callback, and the pinned Azure.Provisioning.AppContainers 1.2.0 metadata exposes ACA volume, mount, and managed-environment-storage models. The complete supported configuration wiring an Azure Files share to this project's published resource remains unverified, so Bicep is recommended as the conservative verified path, not because the project resource is incapable. An unchanged AppHost baseline build passed; no package or AppHost source changes, publish, or Azure activity were performed.
- **2026-10-03:** Aspire 13.6's Azure Storage host integration provides local Azurite Tables and injects `TABLES_CONNECTIONSTRING`; standard ACA should use the system-assigned identity and a pre-created table instead of Azure Files or a single-replica correctness constraint.

📌 Team update (2026-10-03T22:48:50.1676592+02:00): Azure Table Storage is the selected roster persistence for standard ACA; remove SQLite/Azure Files volume and single-replica correctness assumptions. The ACA express target is no longer relevant.