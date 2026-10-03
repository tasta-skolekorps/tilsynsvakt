# Frontend History

- **Owner:** Unavailable (git config user.name could not be read)
- **Project:** Tilsynsvakt guard handbook and roster
- **Stack:** Mobile-first static frontend on GitHub Pages; .NET minimal API backend
- **Created:** 2026-10-03T09:07:58.155Z

## Learnings

- The website is used on a phone by guards at the gym hall; prioritize mobile-first workflows and legible, actionable states.
- User-facing text must be Norwegian Bokmål, with dates and times formatted for Europe/Oslo.
- The backend is the roster master. Never call Spond from the browser or expose secrets, access codes, or unapproved personal data.
- The incident report is only a form until delivery is decided. The guard's responsibility is the gym hall only.

📌 Team update (2026-10-03T22:48:50.1676592+02:00): Azure Table Storage is the selected roster persistence; SQLite has been removed from the implementation. The API contract remains under `/api`, Scalar is available at `/scalar`, and CORS uses the configured GitHub Pages frontend origin.