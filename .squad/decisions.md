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
