# 21 CFR Part 11 / EU GMP Annex 11 — design mapping

This maps regulatory expectations to software features (IDs from PLAN.md §2). It supports, but does not replace,
the customer's computerized-system validation (URS → risk assessment → IQ/OQ/PQ). Procedural controls
(SOPs, training, user management, periodic review, backup restore tests) remain the customer's responsibility.

| Requirement | Clause | How CheckBarcode v2 meets it | Feature IDs |
|---|---|---|---|
| Validation | 11.10(a), Annex 11 §4 | Automated test suite (70 tests, traceability in TRACEABILITY.md), FAT protocol, SHA-256 manifest of released files, config file hashes audited at start-up. | AUD-07 |
| Accurate and complete copies | 11.10(b), §8 | PDF reports (audit, alarm, batch, log) with embedded fonts, `.sha256` file and audit entry per export; filters repeatable. | RPT-01…RPT-06 |
| Record protection / retention | 11.10(c), §7 | SQLite DB with daily online backup and retention; append-only triggers; data kept on uninstall; v1 data imported, not modified. | SYS-03, AUD-01 |
| Limited system access | 11.10(d), §12 | Individual accounts, PBKDF2 passwords, lockout after N failures, idle logout, password expiry, kiosk shell, Exit only for Admin. | USR-01…USR-09, USR-12 |
| Audit trail | 11.10(e), §9 | Computer-generated, UTC time-stamped, who/what/old/new/reason/batch; hash-chained, UPDATE/DELETE blocked; integrity verified at start-up and printed on reports; denied attempts recorded; clock rollback detected. | AUD-01…AUD-08 |
| Operational checks | 11.10(f) | Batch state machine (Created → Running ⇄ Paused → Completed), recipe locked while used, counters reset only when not running. | BAT-03, REC-06, BAT-04 |
| Authority checks | 11.10(g) | Permission matrix per role (data), every action through ActionDispatcher; PLC receives login level. | USR-11, AUD-04, PLC-05 |
| Device checks | 11.10(h) | Configured PLC / camera IPs, heartbeat watchdog both directions, offline alarms with auto-pause. | PLC-01, BAT-03 |
| Training / SOP / documentation control | 11.10(i)(j)(k) | Procedural (customer). Docs delivered: README, FAT, PLC interface, deployment. | — |
| Signature manifestation | 11.50 | Signed record shows printed name, date/time and meaning (vi/en) in audit list and reports. | AUD-05, BAT-10 |
| Signature/record linking | 11.70 | ESignature row stores hash over the audit record hash; cannot be copied to another record. | AUD-03 |
| Unique signatures | 11.100 | Usernames unique forever, users deactivated not deleted. | USR-01, USR-10 |
| Signature components | 11.200(a) | Username + password re-entered at each signing; signer must be the logged-in user; failures count toward lockout. | AUD-05 |
| Password controls | 11.300 | Unique IDs, expiry, history, lockout, admin unlock with forced change. | USR-02…USR-06 |
| Data transfer / interfaces | Annex 11 §5 | Modbus values range-checked and read back; changes made on the PLC side audited as "changed outside PC". | PLC-04, PLC-06 |
| Change of critical data | Annex 11 §9, §10 | Reason mandatory for recipe, cam, camera, permission, policy and settings changes; signature for recipe edit/delete. | REC-04, PLC-04, CAM-05 |
| Printouts | Annex 11 §8.2 | Reports indicate filter, generator user, time and audit-chain integrity status. | RPT-02, AUD-03 |
| Incident / alarm management | Annex 11 §13 | Separate alarm event store, ACK with user, lifecycle visible in report. | ALM-01, ALM-02 |
| Business continuity | Annex 11 §16 | Restart restores batch as Paused with counters and fail data. | BAT-06 |

Known limits: Windows accounts and file-system rights on the PC are outside the application — use a restricted
Windows kiosk account (DEPLOYMENT.md). Tampering with the DB outside the application cannot be prevented, only detected.
