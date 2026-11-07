# TAMS Backend: Status

Last updated: 2026-11-07. Branch of record: `pre-dev` (d6dc7f5), with `feature/prd-transaction-core` on top of it (the rest of the PRD backend, below). `dev` is still the Compose skeleton and has not been promoted.

**Read this first:** `feature/prd-transaction-core` has been run against PostgreSQL 17. Both migrations applied cleanly on an empty database, and an 80-check end-to-end run passed. It covered seeding through the pre-dev endpoints (rooms, stations, categories, assets, users, cards, sections, students, settings), enrollment, checkout, approval, room check-in, return, transit timeout, overdue, missing, settlement, events, audits, notifications and the SignalR handshake. The sweeper paths were forced by moving deadlines in the database. Builds with 0 warnings, 11 tests pass. The fingerprint matcher is a placeholder (see Open decisions #1), so the kiosk cannot be piloted with a real sensor yet.

## Branches

`dev` is clean. `pre-dev` is the integration branch: everything below is merged there and tested together. Each ticket also keeps its own branch so it can be checked out and fixed on its own.

| Ticket | Branch | What it is |
|---|---|---|
| TAMS-27 | `feature/TAMS-27-schema-v1-migrations` | EF Core, `TamsDbContext`, all 29 PRD 7.3 tables, enums, first migration |
| TAMS-44 | `feature/TAMS-44-swagger-error-format` | RFC 7807 problem details with stable `code`, OpenAPI + Scalar, Bearer scheme |
| TAMS-31 | `feature/TAMS-31-staff-authentication` | Login (email only), JWT, refresh, logout, change-password, lockout, dev admin seed |
| TAMS-39 | `feature/TAMS-39-role-based-access` | Policies, default-deny fallback, `PERMISSIONS.md`, endpoint coverage test |
| TAMS-33 | `feature/TAMS-33-account-management-api` | Staff and instructor accounts, reset-password, restore |
| TAMS-38 | `feature/TAMS-38-assets-categories-crud` | Assets, categories, typed tag UIDs, asset photo, restore |
| TAMS-32 | `feature/TAMS-32-rooms-stations-cards-crud` | Rooms, stations, health summary, authorization cards |
| TAMS-40 | `feature/TAMS-40-student-roster-csv-import` | Sections, students, `POST /students:import` |
| TAMS-41 | `feature/TAMS-41-fingerprint-enrollment-api` | Enrollment session state machine, encrypted templates |
| TAMS-42 | `feature/TAMS-42-settings-business-rules-api` | Typed settings catalogue and `ISettings` |
| TAMS-43 | `feature/TAMS-43-asset-qr-endpoint` | `GET /assets/{id}/qr`, `POST /assets/qr-sheet` (PDF) |
| (fixes) | `fix/conformance-api-behaviour` | Post-audit API fixes, `GET /audit-log` |
| (fixes) | `fix/conformance-schema-auth-enrollment` | Migration 2, refresh-token rotation, enrollment fixes |
| (PRD rest) | `feature/prd-transaction-core` | From `pre-dev`. Kiosk checkout, return and event batch/return sessions (F1, F5), approvals (F2), room scans (F3), loans, anomalies, dashboard and occupancy (F4), penalties and resolution (F6), audits, reports, checklist export/import (F7), heartbeats and health sweep (F8), `POST /students`, notifications, SignalR hub. No new migration. |

Only `dev` and `main` exist on origin. Everything else is local until pushed.

Jira (project TAMS): TAMS-19 and TAMS-21 are in Deep Review and assumed approved. Statuses of the tickets above have not been updated; they are still Backlog or In Progress.

## Decisions and why

- **Schema v1 follows `schema-v1-decisions.md`** (in the planning folder): no `middle_name` or `department`, login by email only, change-password yes, forgot-password deferred in favour of admin reset, typed tag UIDs instead of a tap session, admin-only restore, asset photo, `school_year` on late-arrival warnings.
- **Second migration (`SchemaV1Conformance`)** came out of a PRD audit: DB-level defaults from PRD 7.3, checks for STANDARD-loan photo and BORROWABLE tag, partial unique indexes (`WHERE deleted_at IS NULL`) so a soft-deleted record stops blocking its key, `checkout_sessions.finger_index`, one open enrollment session per student and one open session per station, and a `refresh_tokens` table. Create and update checks look at live rows only, so `RESTORE_CONFLICT` can now actually fire.
- **Refresh tokens** are opaque, stored as SHA-256, rotated on every use. Presenting a revoked token revokes all of that user's tokens (`REFRESH_TOKEN_REUSED`). Logout revokes the presented token, or all if none is given. Access tokens live 15 minutes and are not checked against the database per request, so a deactivated user keeps access until the token expires.
- **Routes** are served under `/api` through `UsePathBase`. Both `/api/x` and `/x` route, which keeps the Compose healthcheck and local curl working.
- **Kiosk routes** (`/kiosk/...`) are anonymous to the JWT pipeline and allow-listed in `Policies.AnonymousRoutes`. They identify the station by an `X-Station-Code` header. The IP check is optional and off by default (`Kiosk:EnforceIp`, `Kiosk:AllowedIps`).
- **Fingerprint templates** are encrypted with ASP.NET Data Protection. The key ring is on the `dp-keys` volume. **Lose that volume and every stored template is unreadable.**
- **Audit log** `entity_type` values are lowercase snake_case (`asset`, `asset_category`, `student`, `user`, ...). Keep it that way; `GET /audit-log` filters on exact match.
- **Error codes** worth knowing are in `Http/ErrorCodes.cs`.
- **Authorization** is explicit per endpoint (`.RequireRoles(...)` or a policy). The coverage test (`tests/Tams.Api.Tests`) fails if any endpoint lacks it or is anonymous without being allow-listed.

### Transaction core (`feature/prd-transaction-core`)

- **Kiosk protocol** (PRD 6.2) lives in `Kiosk/`. Every kiosk call sends `X-Station-Code`. A denial is a `FAILED` session with a `failureCode` and the 4 LCD lines, returned with HTTP 200, so the firmware always reads one shape. Problem responses mean protocol errors (bad input, wrong state). `nextStep` tells the kiosk what to collect next. Every mutation locks the session row (`SELECT ... FOR UPDATE`), so the kiosk, the phone and the sweeper never race.
- A new card tap or RETURN start **supersedes** whatever the kiosk had open. A staff-started enrollment blocks the kiosk (`STATION_BUSY`) until it ends. The idle kiosk polls `GET /kiosk/{code}/session` to pick it up.
- The item is held **`AT_COUNTER`** from the tag tap until the loan is created. Any other end (fail, decline, cancel, expiry) puts it back to `IN_STORAGE`.
- **Return** closes the loan on the tag tap, with no confirm step (US-03). It works from `ACTIVE`, `OVERDUE` and `MISSING`. Holds, freezes and liabilities stay for staff.
- **Event batch**: a coordinator's card during their running event opens `EVENT_BATCH`; `type` in the start body forces `CHECKOUT` or `EVENT_RETURN`. Each tap is a loan due at event end + grace. Without a station at the venue, custody is `NOT_REQUIRED` and the asset shows `IN_ROOM_VERIFIED`, because there is no "at event" asset status. Bulk return closes each loan as it is tapped, and Done ends the session.
- **Penalties** (F6) live in `Loans/LoanRules.cs`. One warning per loan. Warning n > `late_arrival_warning_limit` also brings a mark and a freeze. Overdue brings a mark, a hold and a freeze, each at most once per loan. Missing brings a liability at the replacement cost (or the amount given), plus a hold and a freeze if none are active. Standing is `BLOCKED` at `marks_before_block` active marks, `WARNED` with any active warning or mark, otherwise `GOOD`. The cached counters on `students` are recomputed after every change. Nothing lifts automatically, except that settling a liability lifts that loan's hold and freeze (US-10). Every penalty action writes an `audit_log` row.
- **School term** is derived from the local date (Aug-Dec = 1, Jan-May = 2, Jun-Jul = 3, Asia/Manila). `reset-term` clears active warnings from earlier terms and leaves `clear_reason` empty, per schema-v1-decisions section 3.
- **Sweeper** (`LoanSweeper`): every 5 s it expires kiosk sessions and recomputes station health, so a station shows Offline within seconds (US-15). Every 15 s it handles transit timeouts, overdue loans and events that have started. A room tap that beats the sweeper runs the timeout first, so the warning is still issued.
- **Station health** allows 3 s of slack per window (`StationHealthRules.SlackSeconds`, a calibration knob). The station IP stored is the address the API sees, not one the device reports.
- **SignalR** hub at `/hubs/live`. JWT goes in `?access_token=`. Groups are `staff` (ADMIN, TECH_STAFF, CUSTODIAN) and `user:{id}`. Events are sent after commit, with the PRD 6.4 names.
- `max_active_loans_per_student` (FR-1.9, default 1) was added to settings. This resolves the old open decision on that setting.
- Enrollment sessions also answer on the PRD's `/kiosk/sessions/{id}/...` paths. Enrollment views now return `UPPER_SNAKE` status like the rest of the API, which is a change from PascalCase.

## Open decisions (not made, behaviour documented in `PERMISSIONS.md`)

1. **Fingerprint matching (blocks the pilot):** `KioskSessions.MatchAsync` compares decrypted templates for exact equality. Two AS608 captures of the same finger are never byte-identical, so real scans will not match. Pick a matcher. One option: the kiosk uploads the sensor image and the API matches with SourceAFIS (1:N in PostgreSQL, as FR-1.3 asks). That changes the enrollment payload too.
2. **Push notifications:** no FCM yet, and there is no device-token registration route. Notices are `notifications` rows plus SignalR events. A phone in the background gets nothing until push is wired.
3. **Custodian fingerprint rights:** custodians can start enrollments and delete fingerprints; FR-9.4 says "admin-initiated".
4. **`/users` reads:** ADMIN and TECH_STAFF can read (employee numbers included). The original matrix said admin-only.
5. **Event loans:** no coordinator freeze on overdue (the ladder names the approving instructor only), and no liability on missing, because `liabilities.student_id` is required.

## Known gaps

- Not built: room RFID tags and station credentials (the PRD defines neither), push delivery (Open decisions #2), stored report files (`audits.report_file_id` stays null; reports render on request), Excel export (the checklist is CSV, which Excel opens).
- Column names use a `_user_id` suffix where PRD 7.3 says `enrolled_by`, `started_by`, etc. Web and mobile need the final names.
- `sections` and `asset_categories` have no `created_at` or `updated_at`; the PRD is ambiguous.
- Roster CSV columns and limits are our own design (the PRD has none). Check the student-number pattern against the school's real format.
- Several settings ranges are guard rails, not PRD values; only grace (15-30), heartbeat (30-60) and class length (90/120/150/180) come from the PRD.
- Audit-log `from` and `to` bind as `DateTimeOffset`; send an explicit offset.
- TAMS-32's Jira description is a copy of the assets ticket and should be corrected. TAMS-31, 33, 39, 40, 41, 42 and 44 have no acceptance criteria.
- TAMS-41 stores a single merged template per capture. The PRD says "scan twice"; confirm the AS608 merges on the sensor.

## Next steps

1. Pick the fingerprint matcher (Open decisions #1) and replace `KioskSessions.MatchAsync`.
2. Write the kiosk and room-station firmware against `Kiosk/` (README "Kiosk and stations").
3. Wire FCM push and a device-token route (Open decisions #2).
4. Still unexercised from pre-dev: refresh and reuse detection, roster CSV import, the QR sheet and `docker compose up` itself (the smoke run used `dotnet run` against a PostgreSQL container).
5. Settle the remaining open decisions.
6. Push `pre-dev`, the ticket branches and `feature/prd-transaction-core` to origin; open the PRs into `dev`.
7. Update the Jira statuses.
