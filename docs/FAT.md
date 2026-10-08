# Factory Acceptance Test (FAT) protocol — CheckBarcode v2

Run on the target PC (Windows 10 x64, 1920×1080) after installation, with the real PLC and cameras where available.
Without the machine, either set `"driver": "Simulator"` for `plc` / cameras in `config\appsettings.json` (in-process
simulation), or start `PlcSimulator\CheckBarcode.PlcSimulator.exe --port 502` and set the PLC IP to `127.0.0.1`
(keyboard-controlled: run/stop, e-stop, alarms, cam change, speed/temperature offsets). Config changes are recorded
in the audit trail by their hash.

Record the result, tester initials and date for each step. Attach the exported audit report (PDF) of the whole FAT session.

| Step | Feature IDs | Action | Expected result | Result |
|---|---|---|---|---|
| FAT-01 | SYS-05, AUD-07 | Start the application twice. | Full screen, no title bar; second instance does not start. Audit: APP_STARTED, CONFIG_LOADED (hashes), INTEGRITY_CHECK OK. | |
| FAT-02 | USR-08, SYS-05 | Not logged in / as Operator: press Alt+F4, look for Exit button. | Window does not close; Exit button not visible. | |
| FAT-03 | USR-01, USR-05, USR-12 | Login `admin` / default password. | Forced password change, then logout; login with new password works. | |
| FAT-04 | USR-01, USR-10 | Users → create operator `op1`, supervisor `sup1`; deactivate and reactivate `op1` (reason). | Users listed with role; status changes audited with reason. Username cannot be reused. | |
| FAT-05 | USR-02, USR-03, USR-04 | Login `op1` with wrong password 5×. Admin unlocks `op1`. | Locked after 5th attempt, shown in locked list with time and reason. Unlock resets to default password; next login forces change. | |
| FAT-06 | USR-07, USR-09 | Login `op1`, leave idle 3 min. | Automatic logout message; parameter/cam/start buttons disabled; machine and counting continue. | |
| FAT-07 | USR-06 | Set PC date +2 months (or policy expiry 0) and login `op1`. | Password change forced. Admin is exempt. | |
| FAT-08 | USR-11 | Admin → Permissions: remove `Report.Export` from Operator (reason). | Operator can no longer export; audit shows old → new matrix. | |
| FAT-09 | REC-01, REC-02, REC-03, REC-05 | Login `sup1`: create Box recipe (read sample code from camera) and Leaflet recipe. Try duplicate name. Delete one (signature) and re-create same name. | Duplicate refused; delete requires e-signature + reason; name re-usable after delete. | |
| FAT-10 | REC-04, REC-06 | Edit recipe tolerance (signature). Start a batch with it and try to edit again. | Version +1, audit per changed field old → new; edit refused while batch running. | |
| FAT-11 | BAT-01, BAT-02, CAM-04 | Login `op1`: create batch (product, batch no., box + leaflet recipe), Start. | Duplicate batch no. refused; match string pushed to cameras; counters start only after Start. | |
| FAT-12 | CAM-03, BAT-04, BAT-05 | Present good / wrong / no code to each camera. | Last image, read string and result shown; pass/fail/no-read counters correct; finished goods = box pass − leaflet fail − leaflet no-read. | |
| FAT-13 | BAT-03, PLC-01, CAM-01 | Unplug PLC cable, then a camera cable; reconnect. | Alarms *PLC communication lost* / *camera offline*, batch auto-paused, auto reconnect, Resume possible. | |
| FAT-14 | BAT-06 | During a batch kill the process (Task Manager) and restart. | Batch restored as Paused with counters, fail list and fail images intact; operator sessions closed. | |
| FAT-15 | BAT-07, USR-07 | Batch running: `op1` logs out, `op2` logs in and continues. | Batch report lists both operators with their session times. | |
| FAT-16 | BAT-09, PLC-02 | Push actual speed/temperature outside recipe tolerance (simulator keys +/- and T/G). | Software alarm raised; cleared when back in range; values shown on Operation page. | |
| FAT-17 | BAT-08, BAT-10, RPT-01 | End batch (e-signature, reason). | Batch Completed; batch, audit and alarm PDFs generated automatically with `.sha256`; times = actual start → end. | |
| FAT-18 | BAT-04 | Reset counters while Paused (reason). Try while Running. | Allowed when not running and audited with old values; refused while running. | |
| FAT-19 | PLC-04, PLC-05 | Machine → Cam: change 3 cam angles (reason), Write. Enter 400°. | Values written to PLC, readback matches; audit per cam old → new; 400 refused (0…359). | |
| FAT-20 | PLC-06 | Change a cam angle on the PLC/HMI side (simulator key C). | Audit *changed outside PC* with old → new, user = SYSTEM / last user. | |
| FAT-21 | PLC-05 | Login / logout as each role. | `LOGIN_LEVEL` register = 0 / 1 / 2 / 3; PC heartbeat changes every poll. | |
| FAT-22 | ALM-01, ALM-02, ALM-03 | Trigger E-stop and air pressure alarm (simulator keys E, 1–6); ACK without and with login; clear. | Banner shows newest alarm; ACK needs login and records user; lifecycle Active → Acknowledged → Cleared in alarm list and report. | |
| FAT-23 | ALM-03, PLC-03 | Add a new alarm bit to `config\tags.json`, restart. | New alarm appears and is logged without software change; CONFIG_LOADED shows new tags.json hash. | |
| FAT-24 | AUD-01, AUD-03 | Open `data\checkbarcode.db` with DB Browser, try UPDATE/DELETE on AuditTrail; then modify a row via a copy with triggers dropped and restart. | UPDATE/DELETE blocked by trigger; tampering detected: INTEGRITY_CHECK FAILED alarm and on reports. | |
| FAT-25 | AUD-02, AUD-04, AUD-06 | Review audit list after the steps above; switch language. | Every action has user, UTC/local time, old/new, reason, batch; denied attempts present; text re-rendered in vi/en. | |
| FAT-26 | AUD-05 | Login `sup1`, Audit → Review batch (signature). | Review recorded as signed audit event (meaning, user, time). Operator cannot review. | |
| FAT-27 | AUD-08 | Set PC clock back 1 hour, perform an action, set clock forward. | CLOCK_ROLLBACK audit event. | |
| FAT-28 | RPT-02, RPT-03, RPT-07 | Reports: select batch → audit and alarm report; log report with date range. | Default range = batch; adjustable; 24-hour time (dd/MM/yyyy HH:mm:ss). | |
| FAT-29 | RPT-04, RPT-05, RPT-06 | Export to PDF twice; insert USB stick, export; Print with preview. | Repeatable; USB detected without leaving app; `.sha256` next to PDF; export/print audited. | |
| FAT-30 | CAM-02, CAM-05 | Live view on/off, capture; change exposure/gain (reason), write to camera. | Live image; settings audited old → new; persisted in camera. | |
| FAT-31 | CAM-06, SYS-06 | Set image mode FailOnly / All / None; run codes; open Images page and filter. | Images saved per mode; filter by date / barcode / status works. | |
| FAT-32 | SYS-01, SYS-02, SYS-04 | Toggle VI/EN; observe clock and disk indicator; set warning threshold above current free %. | All texts switch; clock 24 h; disk low alarm raised. | |
| FAT-33 | SYS-03 | Settings → Backup now; restart next day. | Backup file in `data\backup`; old backups removed after retention days. | |
| FAT-34 | USR-09, CAM-01 | Not logged in: try disconnect camera, change settings, start batch. | Refused (button disabled or permission message); denied attempt audited. | |
| FAT-35 | — | Touch screen: tap text fields. | On-screen keyboard (TabTip) opens. | |
