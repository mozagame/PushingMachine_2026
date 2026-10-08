# Architecture

```
            ┌──────────────────────────── CheckBarcode.Wpf (net8.0-windows) ───────────────────────────┐
            │ Shell (MainWindow, ShellViewModel) · Pages (Operation, Recipes, Alarms, Audit, Reports,   │
            │ Images, Machine, Users, Settings) · Dialogs (Reason, Signature, Login) · UiKit · Loc       │
            └───────────────┬──────────────────────────────────────────────────────────┬───────────────┘
                            │ commands → ActionDispatcher                              │ creates
                            ▼                                                          ▼
 CheckBarcode.Application (net8.0, BCL only)                         CheckBarcode.Infrastructure.AppHost
   Actions/ActionDispatcher   permission → reason → signature → handler → audit       (composition root)
   Security/ Auth, Session, Users, Permissions, PasswordPolicy                               │
   Audit/AuditTrail           codes + params, clock-rollback check                           │ implements ports
   Plc/TagEngine, MachineService   tags.json driven polling, alarms, parameters             ▼
   Alarms/, Batches/, Recipes/, Cameras/, Settings/, Reports/        Infrastructure: Sqlite (P/Invoke), HashChain stores,
   Ports/ (IPlcClient, ICodeReader, repositories, IAuditStore …)     ModbusTcpClient, PDF writer, files/USB/backup, LegacyImporter
                            │
                            ▼                                        Devices.Cognex: ICodeReader over DataMan SDK (Windows)
 CheckBarcode.Domain (entities, enums, Permissions, Roles)           Simulation: Modbus server + machine model + simulated reader
```

## Principles

- **Dependencies point inward.** Domain has no references; Application references Domain only; device and storage
  details are behind ports. Tests run the real `AppHost` against SQLite and a Modbus TCP simulator.
- **Zero NuGet.** Only the .NET BCL plus the vendor Cognex SDK. SQLite is called through P/Invoke
  (`SqliteNative`), PDF and Modbus are implemented in-house. Builds work offline on the plant PC.
- **One pipeline for every user action.** `ActionDispatcher.ExecuteAsync(code, handler, scope)` reads
  `config/actions.json` and enforces permission, reason and e-signature, then writes the audit record — also for
  denied and failed attempts. A new button therefore needs: one entry in `actions.json`, language text, and the
  handler call. No audit code is written per button.
- **One table for every PLC signal.** `config/tags.json` declares address, bit, type, category, texts. The
  `TagEngine` merges addresses into block reads, debounces, applies deadband and raises `TagChanged`.
  `MachineService` routes by category: Alarm → `AlarmService`, Parameter → audit (incl. changes made outside the PC),
  State → event log (+ audit when flagged), Process → UI / tolerance checks. A new alarm = one JSON entry.
- **Tamper evidence.** `AuditTrail` and `AlarmEvent` are append-only (SQLite triggers) and SHA-256 hash-chained;
  `ESignature` rows hash the signed audit record. `INTEGRITY_CHECK` runs at start-up and every report prints the result.
- **Localization.** Audit records store message codes and parameters, rendered in vi or en at display time.

## Session attribution

| Session state | Recorded user |
|---|---|
| Active | logged-in user |
| Expired (idle logout) | last user, flagged session-expired |
| None | SYSTEM |

## UI implementation note

Views are written in C# (`UiKit` helpers) instead of XAML so that the UI can be compile-checked on Linux CI
(`build/uicheck` builds the WPF and Cognex sources against reference assemblies in `build/uicheck/refs`).
On Windows the normal `CheckBarcode.Wpf` project is built with the .NET 8 Windows Desktop SDK (Visual Studio 2022).

## Extending

| Need | Change |
|---|---|
| New alarm / PLC signal | `config/tags.json` (+ PLC program); run `build/tools/make_plc_doc.py` |
| New audited button | `config/actions.json` + `build/tools/lang_*.py` + call `Dispatcher.ExecuteAsync` |
| New role / permission set | Users → Permissions screen (data, audited) |
| DataMatrix / GS1 serialization | implement `ICodeMatcher`; `BarcodeFormat` already stored per recipe |
| Other PLC protocol | implement `IPlcClient` |
| Other camera | implement `ICodeReader` |
