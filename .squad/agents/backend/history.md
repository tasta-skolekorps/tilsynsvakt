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