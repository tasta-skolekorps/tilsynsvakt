# Tilsynsvakt – Tasta skole gym hall

This repo contains a handbook and a static website for *tilsynsvakter* (guards) in the gym hall at Tasta skole. The guard is the key holder and makes sure the gym hall is used responsibly. The scheme is run by Tasta skolekorps (school band).

## Language and format

- Copilot instructions, code, identifiers, code comments and commit messages: **English**.
- All user-facing content (handbook, website UI text, form labels, error messages): **Norwegian bokmål**.
- Dates as `dd.mm` / `dd.mm.yyyy`, times as `hh:mm`, time zone `Europe/Oslo`.
- Phone numbers displayed as `924 23 946` and linked as `tel:+4792423946`.

## Security – absolute rules

- **Never** write the door code, key box code or any other access code in the repo, code, commits or generated files. In user-facing text write «koden står i permen» instead.
- **Never** put Spond credentials, GitHub tokens or other secrets in client-side code. The website is public.
- Secrets are stored only as GitHub Actions Secrets.
- Contact info from the usage plan (name, phone, email) and the guard roster (date, name, phone) is approved for publishing. Do not publish any other personal data from Spond (e.g. children/members, addresses).

## Guard instructions (source: «Forenklet instruks»)

1. **Before the shift:** The guard receives the *perm* (binder) with keys, instructions and other info (delivered to their home by the previous guard).
2. **Arrive 16:45.** Unlock the gym hall so it is ready for the first activity at 17:00.
   - Turn (gymnastics) uses the **lower entrance** to the changing rooms – open it at 16:45.
   - Main doors – open permanently: `+ 1` → scan card → enter code.
   - Main doors – lock: `+ 2` → scan card → enter code.
3. **Inspection round at start:** Check that all external doors and changing rooms are locked, except the main entrance.
4. **During the shift:** The guard has their own room at the top of the stairs outside the gym hall, and must be available to assist those in the gym hall.
5. **The shift lasts** until the last group has left the gym hall (no later than approx. 22:00). If a group doesn't show up or leaves early, lock the gym hall; the guard may leave if no more groups are coming.
6. **Inspection round at end:** Same round as at start. Lock the lower entrance and main doors.
7. **Handover:** Deliver the perm to the next guard's home (see roster in Spond/the perm). After Thursday the next shift is Tuesday, because board representatives cover Mondays.
8. The roster may change after printing – Spond always has the current version (QR code in the perm).

A key box is **planned** but not established. Refer to it as planned, without location or code.

Questions: Leif Bjarte Johansson, 924 23 946, leif.bjarte@gmail.com.

## Shift days

- **Monday:** board representatives.
- **Tuesday–Thursday:** band parents, per the roster in Spond.
- **Friday:** no activity.
- Outside the periods 01.09–28.11 and 05.01–29.05, only Monday (skolekorps) has activity.
- The guard is responsible for the **gym hall** only, not *musikkaula* (music hall).

## Usage plan 2025–2026 (gym hall)

> Outdated: a usage plan for 2026–2027 is coming. Replace this table when available.

| Day | Time | Group | Contact | Period |
|-----|------|-------|---------|--------|
| Mandag | 17:00–18:30 | Tasta turn | May Brit H. Osaland, 979 81 385, mbho@proconri.no | 01.09–28.11, 05.01–29.05 |
| Mandag | 18:30–22:00 | Tasta skolekorps | Merete Gustavson, 907 41 260, msl@lyse.net | Whole school year |
| Tirsdag | 17:00–20:00 | Tasta turn | May Brit H. Osaland, 979 81 385, mbho@proconri.no | 01.09–28.11, 05.01–29.05 |
| Tirsdag | 20:00–22:00 | Pol Idrettslag | Tor Gunnar Tollaksen, 470 84 832, tor.gunnar.tollaksen@gmail.com | 01.09–28.11, 05.01–29.05 |
| Onsdag | 17:00–21:00 | Tasta turn | May Brit H. Osaland, 979 81 385, mbho@proconri.no | 01.09–28.11, 05.01–29.05 |
| Onsdag | 21:00–22:00 | Fotballgjengen | Christer Waldow, 976 69 565, chrwald@lyse.net | 01.09–28.11, 05.01–29.05 |
| Torsdag | 17:00–21:00 | Tasta turn | May Brit H. Osaland, 979 81 385, mbho@proconri.no | 01.09–28.11, 05.01–29.05 |
| Torsdag | 21:00–22:00 | *Ledig* (free) | – | – |

Musikkaula (not the guard's responsibility): skolekorps Mon 17:45–20:30 and Wed 14–21; Tasta Historielag Wed 19–22 (4–5 meetings per year).

## Website

- Static website hosted on **GitHub Pages**. No server-side code; everything must be servable as static files.
- Mobile first – used on a phone in the hallway by the gym hall.
- Features:
  - **Today's plan:** who is in the gym hall now / next, based on usage plan and date.
  - **Contact list** with tap-to-call.
  - **Roster:** my shifts and who to hand the perm over to.
  - **Incident report:** one generic form where the incident type is selected. Delivery is **not decided** – build only the form, no submission, until clarified.
- **Personalisation** in `localStorage` (no login): the guard selects their name → sees own shifts, next handover and checklist progress for today's shift.
- Data such as the usage plan and roster is stored as JSON in the repo and read by the site.

## Spond integration

- Spond has no official public API. **Never** call Spond from the browser.
- A scheduled GitHub Action fetches the roster using Spond credentials from Secrets and commits JSON (date, name, phone) to the repo.
- Source: group **Tasta Skolekorps – Medlemmer → subgroup Tilsynsvakt**. Each shift is one event with the guard as attendee.
- The website never writes anything back to Spond.
