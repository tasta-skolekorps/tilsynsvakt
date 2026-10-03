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