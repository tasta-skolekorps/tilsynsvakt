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
