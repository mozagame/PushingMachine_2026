# CheckBarcode v2 — Implementation Plan

Status: approved scope (2026-10-06). Author: Apillis automation team.

## 1. Inputs and precedence

| # | Source | Date | Precedence |
|---|---|---|---|
| A | User answers in chat | 2026-10-06 | 1 (highest) |
| B | `Kiểm tra phần mềm máy đóng hộp BTĐ_20260924.xlsx` (customer findings) | 2026-09-24 | 2 |
| C | `Danh sách tính năng - 30102025.xlsx` (original feature list) | 2025-10-30 | 3 |
| D | Decompiled CheckBarcode v1 (`README_DECODE.md`) | 2025-12-25 build | reference only |

Conflicts resolved:

| Topic | C (2025) | B / A (newer) | Decision |
|---|---|---|---|
| Roles | user / supervisor / admin + maintenance permission | B: Operator, Supervisor, Maintenance, Admin. A: Operator, Supervisor, Admin | Seed 3 roles (Operator, Supervisor, Admin). Permissions are data, so a Maintenance role can be added by Admin without code. Maintenance screen permission is granted to Admin by default. |
| Lockout threshold | 3 wrong passwords | B: 5 | Configurable, default **5**. Lock is per user only. Locked until Admin unlocks. |
| Unlock | Unlock re-issues default password | B: admin unlocks, audited | Unlock → password reset to default, must change at next login, audited. |
| Auto logout | 3 min idle | — | Configurable, default **3 min**. Machine keeps running with locked parameters. |
| PLC direction | PC only reads | A: PLC can be modified; cam angles entered on PC | New register map (section 5) with a read block and a write block. |
| E-signature | — | A: end batch, edit/delete recipe, audit review; single level | `actions.json` flags these 4 actions `requireSignature: true`. |
| Pass images | Save fail only | A: setting to choose | Setting `SaveImageMode` = FailOnly (default) / All / None. |
| Codes | Pharmacode | A: DataMatrix/serialization later | `BarcodeFormat` per recipe + pluggable `ICodeMatcher`. v2 ships `ExactMatch`; GS1 parser is a later plug-in. |
| OS / DB | — | A: Windows 10, no SQL server | Local SQLite file, self-contained .NET 8 publish. |
| Screen | — | A: 1920×1080 touch | Fixed 1920×1080 layout, touch-sized controls (min 48 px). |

## 2. Feature list (traceable IDs)

IDs are used in tests and in the traceability matrix (`docs/TRACEABILITY.md`).

**USR – Users and access**
- USR-01 Register user (Admin): username unique forever, full name, role, default password.
- USR-02 Login; per-user failed counter; lock at threshold (default 5); message "account locked".
- USR-03 Locked-user list with lock time and reason (wrong password / locked by admin).
- USR-04 Admin lock/unlock; unlock resets to default password + force change.
- USR-05 Force password change on first login / after reset; logout after change.
- USR-06 Password expiry 2 calendar months (Admin exempt); new ≠ current (and ≠ last N, default N=1).
- USR-07 Auto logout after idle (default 3 min). Machine continues; parameters locked; events in this period carry the last user flagged `session-expired`.
- USR-08 Exit-to-desktop button visible only with `System.Exit` permission (Admin).
- USR-09 Without login: no parameter change, no start/stop, no camera disconnect.
- USR-10 Deactivate user instead of delete; audited.
- USR-11 Permission matrix editable by Admin; audited old → new.
- USR-12 Passwords stored as PBKDF2-SHA256 hash + salt (100 000 iterations).

**REC – Recipes**
- REC-01 Create / list / edit / delete recipes (not Operator).
- REC-02 Recipe fields: name, type (Box / Leaflet), barcode, barcode format, speed setpoint, speed tolerance, temperature setpoint, temperature tolerance.
- REC-03 Soft delete; name unique among active recipes only (re-use after delete).
- REC-04 Version increments on every save; audit per changed field.
- REC-05 Read sample barcode from camera into the recipe form.
- REC-06 Recipe used by a running batch cannot be edited or deleted.

**BAT – Batch / production**
- BAT-01 Create batch: product name + batch number (unique), box recipe and/or leaflet recipe.
- BAT-02 Start requires login + batch parameters; counting and reporting only while Running.
- BAT-03 States: Created → Running ⇄ Paused → Completed. Auto-pause on camera/PLC loss.
- BAT-04 Counters per camera: scanned, pass, fail, no-read; reset (audited, only when not Running).
- BAT-05 Finished goods = box pass − leaflet fail − leaflet no-read (≥ 0).
- BAT-06 Fail barcode list and fail images persist across restart; batch resumes after restart.
- BAT-07 All operators who worked on the batch are recorded (operator sessions), not only the last user.
- BAT-08 Batch time = actual start → actual end.
- BAT-09 Warning when speed / temperature leave recipe setpoint ± tolerance (software alarm).
- BAT-10 End batch requires e-signature; auto-generates batch, audit and alarm PDFs.

**CAM – Cameras**
- CAM-01 Camera list from config; connect / disconnect (disconnect requires login).
- CAM-02 Live view start / stop; capture still during live.
- CAM-03 Receive results (string + image); show last image, result, read string.
- CAM-04 Push match string and validation settings to camera on batch start.
- CAM-05 Camera settings (exposure, gain, trigger delay, interval, timeout, burst) — audited old → new.
- CAM-06 Image storage settings (folder, mode FailOnly/All/None, retention days).

**PLC – PLC link**
- PLC-01 Modbus TCP client, IP/port configurable; auto reconnect; heartbeat both ways.
- PLC-02 Display machine state, actual speed, actual temperature.
- PLC-03 Tag engine: every PLC value defined in `config/tags.json`; categories Alarm / Parameter / State / Process.
- PLC-04 Cam angle setpoints (15 cams × ON/OFF) edited on PC (permission `Machine.CamEdit`), written to PLC, audited with reason.
- PLC-05 PC writes login level, batch running flags, speed/temperature setpoints.
- PLC-06 Changes detected on PLC side (readback ≠ last PC setpoint) are audited as "changed outside PC".

**ALM – Alarms**
- ALM-01 Alarm table separate from audit trail; lifecycle Active → Acknowledged → Cleared.
- ALM-02 Alarm banner + alarm screen; ACK requires login; ACK user recorded.
- ALM-03 New alarm = new entry in `tags.json` (bit or register), no code change.

**AUD – Audit trail (21 CFR Part 11 §11.10(e), Annex 11 §9)**
- AUD-01 Append-only `AuditTrail` table (DB triggers block UPDATE/DELETE).
- AUD-02 Who / what / when (UTC) / old / new / reason / batch / workstation.
- AUD-03 SHA-256 hash chain; integrity check at startup and printed on every report.
- AUD-04 Every user action goes through `ActionDispatcher` (permission → reason → e-signature → handler → audit), including denied attempts.
- AUD-05 Audit review by Supervisor/Admin with e-signature (recorded as audit event).
- AUD-06 Messages stored as code + parameters; rendered in vi or en.
- AUD-07 Machine on/off, software start/stop, PLC/camera connect/disconnect recorded (EventLog + audit where GMP-relevant).
- AUD-08 Clock-rollback detection (UTC now < last record UTC − 2 min) → audit event.

**RPT – Reports**
- RPT-01 Batch report: ID, start, end, batch no, operators, recipe, barcode, total, pass, fail, no-read, fail barcode list.
- RPT-02 Audit report and Alarm report: default time range = selected batch; adjustable.
- RPT-03 Log report (box / leaflet) with date range filter.
- RPT-04 Export filtered data to PDF, repeatable; PDF hash stored and written as `.sha256`.
- RPT-05 Export to USB without leaving the application; auto-detect removable drive.
- RPT-06 Print with in-app preview.
- RPT-07 24-hour time format everywhere.

**SYS – System**
- SYS-01 Vietnamese / English switch at runtime.
- SYS-02 Date-time clock on the main screen.
- SYS-03 Daily backup of the database (SQLite online backup) to backup folder; retention configurable.
- SYS-04 Disk free-space display and warning.
- SYS-05 Single instance; kiosk-style full screen.
- SYS-06 Image management screen: list, filter by date / barcode / status, view.

## 3. Architecture

```
CheckBarcode.Domain          entities, enums, value objects, domain errors (no dependencies)
CheckBarcode.Application     services + ports (interfaces): Auth, Users, Recipes, Batches,
                             Alarms, Audit, Reports, Machine (cams/setpoints), ActionDispatcher,
                             TagEngine, Localization, Clock
CheckBarcode.Infrastructure  SQLite (P/Invoke, no NuGet), repositories, migrations,
                             Modbus TCP client, PDF writer (TrueType embedding),
                             file storage, USB detection, backup, JSON config loaders
CheckBarcode.Devices.Cognex  ICodeReader adapter over Cognex DataMan SDK (Windows only)
CheckBarcode.Simulation      Modbus TCP server + machine model + simulated code reader (tests, demos)
tools/CheckBarcode.PlcSimulator  console PLC simulator for FAT / training
CheckBarcode.Wpf             WPF MVVM UI, 1920×1080, code-defined views
CheckBarcode.Tests           self-contained test runner (no NuGet), unit + integration
```

Rules:
- Dependencies point inward: Wpf → Application → Domain; Infrastructure implements Application ports.
- No static mutable state; one composition root (`Infrastructure/AppHost.cs`, used by the WPF app and the integration tests).
- **Zero NuGet dependencies** for Domain / Application / Infrastructure / Tests. Only BCL. Reason: reproducible offline builds on the plant PC and smaller validation surface (GAMP 5 Cat. 4/5).
- All user actions → `ActionDispatcher.ExecuteAsync(actionCode, args, handler)`.
- All PLC values → `TagEngine` → events → `AlarmService` / `AuditService` / UI.

### 3.1 Action pipeline

```
UI command → ActionDispatcher
   1. load ActionDefinition from actions.json
   2. check session + permission           → deny → audit ACTION_DENIED
   3. ask reason (if requireReason)        → UI prompt
   4. e-signature (if requireSignature)    → re-enter username + password, meaning
   5. run handler (returns AuditEntry list: field/old/new)
   6. append audit entries (hash chained)  → also on handler failure (ACTION_FAILED)
```

### 3.2 Tag engine

`config/tags.json` entries: `id, category, area, address, bit?, type, scale, unit, deadband, debounceMs, activeWhen, severity, text{vi,en}, direction(Read|Write), pairedReadTag?`.
Polling cycle 250 ms (configurable). Block reads are computed automatically from the address set.

## 4. Database schema (SQLite, file `data/checkbarcode.db`, WAL, UTC ISO-8601 text timestamps)

Compatible with SQLite ≥ 3.21 (no UPSERT / RETURNING / window functions).

```
SchemaVersion(Version INTEGER, AppliedUtc TEXT)
Role(Code TEXT PK, NameVi, NameEn, IsSystem INTEGER)
RolePermission(RoleCode, Permission, PRIMARY KEY(RoleCode, Permission))
User(Id INTEGER PK, Username TEXT UNIQUE COLLATE NOCASE, FullName, RoleCode, Status TEXT,
     PasswordHash, PasswordSalt, PasswordChangedUtc, MustChangePassword INTEGER,
     FailedCount INTEGER, LockedUtc, LockReason, CreatedUtc, CreatedBy)
PasswordHistory(Id PK, UserId, Hash, Salt, CreatedUtc)
Recipe(Id PK, Name, Type, Barcode, BarcodeFormat, SpeedSetpoint REAL, SpeedTolerance REAL,
       TempSetpoint REAL, TempTolerance REAL, Version INTEGER, Status, CreatedUtc, UpdatedUtc)
       + partial unique index on Name WHERE Status='Active'
Batch(Id PK, BatchNo TEXT UNIQUE, ProductName, BoxRecipeId, BoxRecipeVersion,
      LeafletRecipeId, LeafletRecipeVersion, Status, CreatedUtc, StartUtc, EndUtc,
      CreatedBy, EndedBy)
BatchOperator(Id PK, BatchId, Username, FromUtc, ToUtc)
BatchCounter(BatchId, Camera, Scanned, Pass, Fail, NoRead, PRIMARY KEY(BatchId, Camera))
InspectionResult(Id PK, BatchId, Camera, Utc, ReadString, Result, ImagePath)
AuditTrail(Seq INTEGER PK, Utc, Username, Role, SessionState, ActionCode, ObjectType,
           ObjectId, Field, OldValue, NewValue, Reason, MessageArgs, BatchId,
           Workstation, PrevHash, Hash)                       -- append-only (triggers)
ESignature(Id PK, AuditSeq, Username, Meaning, Utc)            -- append-only
AlarmEvent(Seq PK, AlarmId, Severity, State, Utc, Username, BatchId, PrevHash, Hash) -- append-only
EventLog(Id PK, Utc, Source, Code, Data)
Setting(Key PK, Value)                                          -- changes audited
ReportFile(Id PK, Kind, BatchId, Path, Sha256, CreatedUtc, CreatedBy)
```

Alarm state is event-sourced: every transition (Active, Acked, Cleared) is a new hash-chained row.

## 5. Interfaces ("API")

This is a desktop application; external interfaces are:

**5.1 Modbus TCP (PC = client, PLC = server, unit 1, FC03 read / FC16 write)** — default map, fully defined in `tags.json`:

| Reg | Dir | Tag | Notes |
|---|---|---|---|
| 0 | R | PLC_HEARTBEAT | PLC increments every cycle |
| 1 | R | MACHINE_RUNNING | 0/1 |
| 2 | R | ESTOP_ACTIVE | 0/1 |
| 3 | R | ALARM_WORD_1 | bit0 inverter, bit1 turntable misalign, bit2 air pressure, bit3 maintenance due, bit4 indate temp high, bit5 indate temp low |
| 4 | R | STATUS_WORD | bit0 leaflet feed enabled, bit1 box suction enabled, bit2 indate on |
| 5 | R | SPEED_ACTUAL | boxes/min |
| 6 | R | TEMP_ACTUAL | °C |
| 10–39 | R | CAM_xx_ON/OFF_ACT | 15 cams readback (deg) |
| 100 | W | PC_HEARTBEAT | |
| 101 | W | LOGIN_LEVEL | 0 none, 1 Operator, 2 Supervisor, 3 Admin |
| 102 | W | BOX_CAM_SCANNING | 0/1 |
| 103 | W | LEAFLET_CAM_SCANNING | 0/1 |
| 104 | W | SPEED_SETPOINT | from recipe |
| 105 | W | TEMP_SETPOINT | from recipe |
| 110–139 | W | CAM_xx_ON/OFF_SP | cam setpoints from PC |

**5.2 Code reader port** `ICodeReader`: Connect, Disconnect, StartLive/StopLive, Capture, ApplyMatchString, Get/SetSettings, events ResultArrived(ReadString, Image, Utc), ConnectionChanged.

**5.3 Application services** (public methods called by ViewModels): `AuthService.Login/Logout/ChangePassword`, `UserService.Create/Update/Lock/Unlock/Deactivate`, `RecipeService.Create/Update/Delete/List`, `BatchService.Create/Start/Pause/Resume/End/Restore`, `AlarmService.Ack`, `AuditService.Query/Verify/Review`, `ReportService.Batch/Audit/Alarm/Log → ReportDocument`, `ExportService.ToPdf/ToUsb`, `MachineService.WriteCams`.

## 6. Task breakdown

1. Solution skeleton, build props, zero-dependency policy.
2. Domain model.
3. SQLite native wrapper + migrations + repositories.
4. Audit store with hash chain, verification, triggers.
5. Auth / users / permissions / password policy / lockout / sessions / idle.
6. ActionDispatcher + actions.json + e-signature.
7. Recipes with versioning and soft delete.
8. Batches with operators, counters, restore.
9. Modbus TCP client + tag engine + tags.json + alarm service.
10. Code reader port, simulator, Cognex adapter.
11. Reports model + PDF writer + USB export.
12. Localization vi/en.
13. WPF UI (main, batch, recipes, alarms, audit, reports, images, users, settings, cams).
14. Tests for every feature ID; simulator-based integration tests.
15. Deployment: publish script, installer script, CI workflow, operator/admin docs.

## 7. Testing strategy

- `CheckBarcode.Tests` console runner: `dotnet run --project tests/CheckBarcode.Tests` → exit code 0 = all pass.
- Unit tests on services with in-memory fakes; integration tests with real SQLite file, Modbus simulator server on localhost, simulated code reader.
- PDF output verified by re-parsing (structure + extracted text through ToUnicode).
- UI: compile-checked; FAT checklist in `docs/FAT.md` for manual runs on Windows.

## 8. Deployment

- Target: Windows 10 x64, .NET 8 self-contained, single folder `C:\CheckBarcode` (exe + `config\` + `data\`).
- `build/publish.ps1`: run tests, publish self-contained win-x64 (native SQLite and Cognex SDK DLLs copied by the project), publish PLC simulator, SHA256SUMS, zip.
- `build/ci.sh`: Linux CI (build, tests, WPF compile check against reference assemblies, generated docs).
- `build/installer.iss`: Inno Setup script (installs to `C:\CheckBarcode`, desktop shortcut, auto-start, config never overwritten, data kept on uninstall).
- `.github/workflows/build.yml`: ubuntu (ci.sh) → windows-latest (publish.ps1 + Inno Setup) → artifacts.
- Validation support docs: `docs/COMPLIANCE.md` (Part 11 / Annex 11 clause ↔ feature ID), `docs/TRACEABILITY.md` (feature ID ↔ test ↔ FAT step), `docs/FAT.md`, `docs/PLC_INTERFACE.md`.
- Data migration tool (v1 → v2): import recipes from v1 `MyDatabase.db`; users are re-created (v1 passwords are plain text and are not migrated).
