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