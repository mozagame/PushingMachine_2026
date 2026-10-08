# Deployment

## Build machine (Windows)

1. Visual Studio 2022 17.8+ or .NET 8 SDK (with Windows Desktop). Inno Setup 6 for the installer.
2. `powershell -ExecutionPolicy Bypass -File build\publish.ps1 -Version 2.0.0`
   → runs tests, publishes `artifacts\CheckBarcode-2.0.0-win-x64\` (app, PlcSimulator, docs, SHA256SUMS.txt) and a zip.
3. `iscc /DVersion=2.0.0 build\installer.iss` → `artifacts\CheckBarcode-2.0.0-setup.exe`.

CI does the same: `.github/workflows/build.yml` (Linux: `build/ci.sh` tests + UI compile check; Windows: package + installer).

## Target PC (Windows 10 x64, 1920×1080 touch)

1. Run the setup as administrator (installs to `C:\CheckBarcode`, auto-start, desktop shortcut). No .NET install needed.
2. Edit `C:\CheckBarcode\config\appsettings.json`: machine name/code, customer, PLC IP, camera IPs. Configuration
   files are never overwritten by an update; their SHA-256 is written to the audit trail at every start.
3. Network: PC, PLC and cameras on the machine LAN. Allow inbound/outbound TCP 502 (Modbus) and 23 (DataMan).
4. First start: login `admin` / `Abc@1234` → forced password change → create users (Users page).
5. Optional: Settings → *Import v1 recipes* from the old `MyDatabase.db` (users are not migrated: v1 passwords were plain text).
6. Execute `docs/FAT.md` and keep the signed protocol and exported audit report.

## Windows hardening (procedural, required for Part 11)

- Run the application under a standard (non-admin) Windows account; enable Windows *Assigned Access* / kiosk or
  a shell replacement so operators cannot reach Explorer. Only administrators may access `C:\CheckBarcode\data`.
- Disable Windows automatic time change by users; synchronise time from a trusted NTP source (clock rollback is audited).
- Disable USB mass-storage autorun; the application exports to USB itself.
- Back up `data\backup` to a network location as per site SOP and test restores.

## Data folder

| Path | Content |
|---|---|
| `data\checkbarcode.db` | SQLite database (users, recipes, batches, audit trail, alarms, results) |
| `data\images\<day>\<batch>\` | camera images (mode FailOnly / All / None) |
| `data\reports\` | auto-generated PDFs + `.sha256` |
| `data\backup\` | daily online backups |
| `data\logs\` | technical log (not GMP record) |

## Update

Run the new setup over the old one. The database schema migrates automatically on start (versioned migrations);
`config` and `data` are kept. Record the update under change control.

## Rollback

Uninstall, install the previous setup, restore `data\checkbarcode.db` from `data\backup` if the schema version changed.

## Known risks to verify at FAT

- Cognex DataMan SDK DLLs were built for .NET Framework; they load on .NET 8 (Windows) via compatibility mode — verify live
  image, results and DVALID commands with the real DM262 (FAT-11, FAT-12, FAT-30). If incompatible, switch the
  Cognex project to `net48` hosted out-of-process or use the DataMan TCP/telnet protocol in `CognexCodeReader`.
- PLC register map (`docs/PLC_INTERFACE.md`) is a proposal; the PLC programmer must implement it.
