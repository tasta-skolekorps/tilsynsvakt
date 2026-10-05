# Spond roster sync

This .NET 10 console tool reads the backend roster and synchronizes owned Spond
events using the user-supplied schema verified on 05.10.2026. `DRY_RUN` defaults
to `true`; explicit `false` enables creation, quiet deletion and metadata-only
updates. Empty plans and unmatched/missing-profile skips exit 0. No application
or live service was run while implementing these contracts; tests use local mocks.

## Current Contract

- List `/core/v1/sponds` with `includeComments=true`, `includeHidden=false`,
  `addProfileInfo=true`, `scheduled=true`, `order=asc`, `max=100`, `groupId`,
  `minStartTimestamp`, and `maxStartTimestamp`. The start window runs from the
  earliest sync date's Oslo midnight to midnight after the latest sync date,
  converted to UTC. Pagination overlaps the last start timestamp and deduplicates
  event IDs; descending starts, starts before the cursor, or a full page without
  cursor progress fail closed. Multiday events need not have ordered end times.
  Client-side group, ownership-marker and date checks remain required.
  Olen/Spond v1.2.1 `get_events` verifies the start filters, `groupId`, `max`,
  `scheduled` and `includeHidden`; `includeComments`, `addProfileInfo` and `order`
  retain the earlier user-established verification, not evidence from that source.
- Match normalized backend phones to guardians across every matching child whose
  `member.subGroups` contains the target subgroup ID string. Invite all of those
  guardians by profile ID, deduplicate shared profiles, and never invite children.
  Any required missing profile skips the whole shift with a sanitized warning.
- Create `POST /core/v1/sponds`: guardian objects contain `email`, `phoneNumber`,
  `profileId`; `groupMembers` is empty, and create subgroup IDs are strings.
  Owner comes only from `/core/v1/profile.id`. Exact heading/description/marker,
  16:45-22:00 Oslo, string `meetupPrior: "5"`, invitation seven Oslo wall-clock
  days earlier, `autoAccept: false`, and `REMIND_48H_BEFORE` are emitted.
- Changed guardian set: quietly DELETE the owned future event, then CREATE its
  replacement. No recipient-removal contract is invented. Preflight validates
  all payloads before writes; the replacement is not atomic and has no retries.
- Metadata-only POST preserves invitation, location ID, owners, attachments and
  tasks, uses the verified update shape, and never includes recipients/responses.
  Fresh reads check ownership, group and actual future start before update/delete.
- Explicit guardian `profileId` readback produces a normalized state for local
  idempotence. The supplied GET inventory does not establish guardian item fields;
  unreadable recipient state fails closed, rather than claiming live idempotence.

**Unverified:** location `{feature}` without `id` needs first-real-run confirmation;
`inviteTime` on CREATE is not established by its observed event/update usage;
the additional three-day reminder remains open. The create builder emits the
requested location and invite time, but tests do not establish server acceptance
or notification behavior. Only `REMIND_48H_BEFORE` is used, not a 72-hour option.

## Environment

- `SPOND_USERNAME`, `SPOND_PASSWORD`: required even for dry-run. Configure only
  in GitHub Actions Secrets; never send credentials to an agent.
- `API_BASE_URL`: required HTTP(S) origin, without credentials, path, query, or fragment.
- `SPOND_GROUP_NAME`: defaults to `Tasta Skolekorps - Medlemmer`.
- `SPOND_SUBGROUP_NAME`: defaults to `Tilsynsvakt`.
- `SPOND_SYNC_DATES`: optional comma-separated `yyyy-MM-dd`; pilot `2026-11-26`.
  Empty means the full current period. Limits intersect today through 29 May or
  28 November. Outside 5 January-29 May / 1 September-28 November, no dates apply.
- `DRY_RUN`: `true` or `false`, case-insensitive; defaults to `true`.

Run: `dotnet run --project src/Tilsynsvakt.SpondSync -c Release`.
Missing configuration exits 1 without network access. Other errors are sanitized.
Logs contain dates, actions, and backend guard names only. No Spond data is persisted.

## Source Verification

Historical source-research record below, retained for traceability. Its earlier
mutation blockers, single-child matching and planner behavior are superseded by
the Current Contract above; pinned-client absence is not private-API absence.

Reviewed public pinned sources, not live authenticated traffic:

- [Olen/Spond v1.2.1 base.py](https://raw.githubusercontent.com/Olen/Spond/v1.2.1/spond/base.py)
- [Olen/Spond v1.2.1 spond.py](https://raw.githubusercontent.com/Olen/Spond/v1.2.1/spond/spond.py)
- [Olen/Spond event template](https://raw.githubusercontent.com/Olen/Spond/v1.2.1/spond/_event_template.py)
- [Olen/Spond issue 229](https://github.com/Olen/Spond/issues/229)
- [Spond.API v3.0 client](https://raw.githubusercontent.com/ArizonaGreenTea05/Spond.API/v3.0/Spond.API/Services/SpondClient.cs)
- [Spond.API v3.0 endpoint construction](https://raw.githubusercontent.com/ArizonaGreenTea05/Spond.API/v3.0/Spond.API/Models/CommonData.Core.V1.cs)
- [Spond.API group model](https://raw.githubusercontent.com/ArizonaGreenTea05/Spond.API/v3.0/Spond.API/Models/SpondGroup.cs)
- [Spond.API member model](https://raw.githubusercontent.com/ArizonaGreenTea05/Spond.API/v3.0/Spond.API/Models/SpondMember.cs)
- [Spond.API event model](https://raw.githubusercontent.com/ArizonaGreenTea05/Spond.API/v3.0/Spond.API/Models/SpondEvent.cs)
- [Spond.API reminder enum](https://raw.githubusercontent.com/ArizonaGreenTea05/Spond.API/v3.0/Spond.API/Enums.cs)
- [Olen pinned directory](https://github.com/Olen/Spond/tree/v1.2.1/spond)
- [Olen newer client, pinned commit](https://raw.githubusercontent.com/Olen/Spond/86df2230bf641b0cc6919970d96dd1106f277170/spond/spond.py)
- [Olen meetup read example](https://raw.githubusercontent.com/Olen/Spond/v1.2.1/examples/ical.py)
- [Olen public examples](https://github.com/Olen/Spond/tree/v1.2.1/examples)
- [Olen public tests](https://raw.githubusercontent.com/Olen/Spond/v1.2.1/tests/test_spond.py)
- [Spond.API services directory](https://github.com/ArizonaGreenTea05/Spond.API/tree/v3.0/Spond.API/Services)
- [Spond.API update request](https://raw.githubusercontent.com/ArizonaGreenTea05/Spond.API/v3.0/Spond.API/Models/SpondEventUpdateRequest.cs)
- [Spond.API location model](https://raw.githubusercontent.com/ArizonaGreenTea05/Spond.API/v3.0/Spond.API/Models/SpondEventLocation.cs)
- [Spond.API console example](https://raw.githubusercontent.com/ArizonaGreenTea05/Spond.API/v3.0/Spond.API.Test/Program.cs)

### Pinned Directory Inventory

Inspected actual public directory pages, not inferred filenames. Recursive public
GitHub tree API requests through both the fetch tool and unauthenticated
PowerShell returned 403 (the latter reported rate-limit exhaustion). The pinned
directory pages and public raw files remained accessible. No authentication,
credentials, live Spond requests or private resources were used.

Olen/Spond v1.2.1 root identifies commit
`2d8addc4b8ae0dcf3c733ea8cd159ee4991854c3`. Complete relevant inventories:

- `spond/`: `__init__.py`, `_event_template.py`, `base.py`, `club.py`, `spond.py`.
  There are no subdirectories or mixins; no `events.py`, `_events.py` or `event.py`.
- `examples/`: `attendance.py`, `config.py.sample`, `groups.py`, `ical.py`,
  `manual_test_functions.py`, `transactions.py`.
- `tests/`: `__init__.py`, `test_spond.py`.

Read every listed package, example and test Python source except the configuration
sample (not needed for transport evidence). In `spond/spond.py`, event methods are
`get_events`, `get_event`, `update_event`, `get_event_attendance_xlsx` and
`change_response`; no event create/delete method exists. `club.py` only implements
`get_transactions`. `manual_test_functions.py` has `delete=False` for local output
handling, not an event DELETE. Tests cover event lookup, response PUT, export,
groups, posts and login, not event create/delete or scheduled guardian invitations.
`ical.py` reads `meetupTimestamp`; `_event_template.py` does not write it.

Spond.API v3.0 complete relevant inventories:

- `Spond.API/Services/`: `SpondClient.cs` only.
- `Spond.API/Models/`: `CommonData.2.1.cs`, `CommonData.Core.V1.cs`, `SpondChat.cs`,
  `SpondComment.cs`, `SpondCompleteUserProfile.cs`, `SpondDeclineMessage.cs`,
  `SpondEvent.cs`, `SpondEventLocation.cs`, `SpondEventOwner.cs`, `SpondEventTask.cs`,
  `SpondEventUpdateRequest.cs`, `SpondGroup.cs`, `SpondLoginInformation.cs`,
  `SpondMember.cs`, `SpondPost.cs`, `SpondRole.cs`, `SpondSubGroup.cs`,
  `SpondTransaction.cs`, `SpondUserGlobalPushPreferences.cs`,
  `SpondUserGroupPushPreferences.cs`, `SpondUserOptionalSettings.cs`,
  `SpondUserPreferences.cs`, `SpondUserProfile.cs`.
- `Spond.API.Test/`: `Program.cs`, `Spond.API.Test.csproj`.

Read the service, both endpoint builders, every event model listed above,
group/member models, `Enums.cs` and the console example. Event service methods are
`GetEvents`, `GetEvent`, `UpdateEvent`, `GetEventAttendance` and `ChangeResponse`;
no event creation/deletion implementation exists. `UpdateEvent` explicitly POSTs
to `GetEventUrl(eventId)`, whose Core.V1 implementation returns
`core/v1/sponds/{eventId}`. Its anonymous payload has no recipients, meetup or
invitation scheduling fields. `SpondEventRecipients` exposes only `Group`;
accepted/declined/unanswered IDs in `SpondEventResponses` are response data, not
proof of a guardian-only invitation contract. `CommonData.2.1.cs` supplies the
alternative `/api/2.1/sponds/{eventId}` route, not a create/delete implementation.

Directory inventory is complete for the paths above; source inspection is scoped
to transport/event evidence, not a claim that every unrelated model was reviewed.
The newer previously inspected Olen pinned client also has no create/delete
methods. Absence here concerns these pinned clients, not the capabilities of
Spond's private API itself.

Verified login: `POST /core/v1/auth2/login` with `email`, `password`, reading
`accessToken.token` as the Bearer token. No old-login fallback or OTP handling.

Verified group read: `GET /core/v1/groups/`, root array; `id`, `name`,
`subGroups[{id,name}]`, `members[{id,subGroups:[id],guardians:[{id,phoneNumber}]}]`.
Exact group and subgroup names must each match once. Phone matching only considers
guardians of a child in that subgroup; multiple matching children fail closed.
All guardians of the unique child are selected, never the child or entire subgroup.
These are internal domain identifiers, NOT a verified wire recipient payload.

Verified event read: `GET /core/v1/sponds/` with `groupId`, `scheduled=true`,
`includeHidden=true`, `order=asc`, `max=100`, `minStartTimestamp`, `maxStartTimestamp`.
Query each selected Oslo day separately. No event pagination cursor was verified;
a saturated day fails closed rather than assuming a complete list.

Verified single-event read: `GET /core/v1/sponds/{id}` in Spond.API v3.0
`Services/SpondClient.cs` and `Models/CommonData.Core.V1.cs`.

Verified generic update: `POST /core/v1/sponds/{id}` in both clients. The Olen
`_event_template.py` supplies `id`, `heading`, `description`, `spondType`,
`startTimestamp`, `endTimestamp`, `commentsDisabled`, `maxAccepted`, `rsvpDate`,
`location`, `owners`, `visibility`, `participantsHidden`, `autoReminderType`,
`autoAccept`, `payment`, `attachments`, `tasks`. Location is an object with
`id`, `feature`, `address`, `latitude`, `longitude`, not the domain string.
Recipient replacement is NOT verified or implemented. The response model only
exposes `recipients.group`; it does not establish selected guardian readback.
`autoAccept=false` is source-confirmed, not live-tested; no response writes occur.
Create and delete endpoints/payloads were NOT established by these pinned sources;
the update POST is not evidence for creation, nor is `/responses/{id}` a
guardian invitation endpoint.
Guardian-only recipients plus subgroup context and scheduled invitation JSON are
NOT verified. Neither `scheduledTime` nor `inviteTime` appears in these event
write/read models or the template. The `scheduled=true` query is only a list
filter, not proof of a scheduled invitation payload. Do not substitute child IDs
or subgroup-wide invitations. The Olen iCal example confirms `meetupTimestamp`
readback, but the update template/request omits that field.

Verified reminder enum values: `DISABLED`, `REMIND_24H_BEFORE`,
`REMIND_48H_BEFORE`, `REMIND_72H_BEFORE`. Standard unanswered reminder semantics
plus an additional three-day reminder are NOT established. No reminder writes
are made; a single 72-hour enum must not be claimed to implement both reminders.

## Limited Metadata Transport

`GetEventAsync` reads fresh single-event JSON in memory. `UpdateEventMetadataAsync`
changes only `heading` and `description`. Before POST it verifies the exact marker
date, group ID, actual start instant (strictly future), matching Oslo start date,
and single-event type. It copies every template field from the current response
without defaults, preserving payments, attachments, tasks and reminder settings;
missing template fields fail closed. It never sends `recipients` or `responses`.
Unchanged heading/description return `false` without POST. A changed event with
non-null `meetupTimestamp`, `scheduledTime`, or `inviteTime` is blocked because
preserving those fields is not established by the verified update template.
The response must confirm the event ID and changed metadata. There are no retries.

Credential-free in-memory HTTP probe: first metadata update sends one POST;
second read/update sends none; protected fields remain unchanged in the mock.
Unowned, wrong-group, past-start, meetup, scheduledTime and inviteTime cases each
block before POST. This verifies local transport behavior only, not server-side
response preservation, notification behavior or live scheduled-event semantics.
All login, groups, events and mutation source findings remain NOT live-tested.

Targeted evidence completion validation (05.10.2026):
`dotnet build src/Tilsynsvakt.SpondSync/Tilsynsvakt.SpondSync.csproj --no-restore`
and `dotnet build Tilsynsvakt.slnx` succeeded.
`dotnet test Tilsynsvakt.slnx --no-build` passed 37 tests, skipped two opt-in
Azurite contracts, and failed none. No sync test project was present at this check;
no tests were authored or edited. The credential-free in-memory HTTP probe was
rerun successfully: two fresh GETs, one POST, second-run no-op, protected mock
fields unchanged and all six refusal cases sending zero POSTs. This pass changed
only this evidence record and the backend's own history/decision inbox; existing
transport code and unrelated worktree changes were preserved.

## Planner Handoff

Public API in namespace `Tilsynsvakt.SpondSync`:

`SyncPlanner.Plan(IReadOnlyList<RosterShift> roster, IReadOnlyList<ExistingEvent> existing,
TargetGroup group, IReadOnlySet<DateOnly> dates, DateOnly today)`
returns `IReadOnlyList<SyncAction>` with `Create`, `Update`, `Delete`, `SkipUnmatched`.
The project grants internals to `Tilsynsvakt.SpondSync.Tests` as well.

`ExistingEvent(string Id, string? Description, EventSpec? VerifiedState)` owns a
date only through an exact description marker line. Equivalent verified states
produce no action, including guardian order differences. Runtime reads set
`VerifiedState=null`: recipient/invitation readback is unverified, so runtime
idempotence cannot yet be claimed. An existing event is conservatively proposed
for update, never written by the executable. Full normalized `EventSpec` cannot
be constructed safely from `recipients.group` or response member IDs. Marker
parsing now derives its date offset/total length from the marker prefix rather
than incorrect hard-coded offsets. Cancellation must be an explicit open roster row;
a missing row for an owned event fails closed.

`SyncPlanner.DesiredEvent` supplies the required title, location, exact Norwegian
description, meet/start/end UTC times and invite time seven Oslo wall-clock days
before start. `EventSpec` is a domain plan, not a mutation DTO. DST conversion uses
`TimeZoneInfo` for Europe/Oslo. `SyncCalendar.SelectDates`, `At`,
`SyncPlanner.OwnedDate`, `NormalizePhone`, and `MatchGuardians` are independently testable.

Remaining blocker: source evidence for a guardian-only recipient selection that
retains subgroup context without inviting the subgroup, recipient replacement
without resetting responses/reinviting unchanged guardians, scheduled invitation
write/readback (seven days before), create/delete contracts, and standard
unanswered plus extra three-day reminder semantics. No dangerous payload is
inferred, no credentials requested, and no production readiness is claimed.
Environment, pilot `SPOND_SYNC_DATES=2026-11-26`, event location and public-log
contracts are unchanged. Unmatched shifts still skip without deleting owned events.