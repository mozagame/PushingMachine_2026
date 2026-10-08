"""Generates docs/PLC_INTERFACE.md from config/tags.json (single source of truth for the PLC programmer)."""
import json, os
root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
tags = json.load(open(os.path.join(root, "config", "tags.json"), encoding="utf-8"))
app = json.load(open(os.path.join(root, "config", "appsettings.json"), encoding="utf-8"))
out = []
out.append("# PLC interface — Modbus TCP register map\n")
out.append("Generated from `config/tags.json` by `build/tools/make_plc_doc.py`. Do not edit by hand.\n")
p = app["plc"]
out.append(f"- PC = Modbus TCP **client**, PLC = **server** at `{p['ip']}:{p['port']}`, unit id {p['unitId']}.")
out.append(f"- PC reads holding registers with FC03 every {p['pollMs']} ms and writes with FC16. Addresses are 0-based (`40001` = address 0).")
out.append(f"- `PLC_HEARTBEAT` must change at least every {p['heartbeatTimeoutMs']} ms, otherwise the PC raises *PLC communication lost*.")
out.append("- The PLC should stop scanning / lock parameters when `PC_HEARTBEAT` stops changing (PC down) and should only allow HMI parameter changes / Start when `LOGIN_LEVEL` > 0.\n")
for direction, title in (("Read", "PLC → PC (PLC writes, PC reads)"), ("Write", "PC → PLC (PC writes, PLC reads)")):
    out.append(f"## {title}\n")
    out.append("| Address | Bit | Tag | Type | Category | Unit / range | Meaning (vi) | Meaning (en) |")
    out.append("|---|---|---|---|---|---|---|---|")
    for t in sorted([t for t in tags if t.get("direction", "Read") == direction], key=lambda t: (t["address"], t.get("bit") if t.get("bit") is not None else -1)):
        rng = t.get("unit", "")
        if "min" in t or "max" in t:
            rng += f" {t.get('min','')}…{t.get('max','')}"
        extra = f" (alarm when {t.get('activeWhen','!= 0')})" if t["category"] == "Alarm" else ""
        out.append(f"| {t['address']} | {'' if t.get('bit') is None else t['bit']} | `{t['id']}` | {t.get('type','UInt16')} | {t['category']}{extra} | {rng.strip()} | {t['text']['vi']} | {t['text']['en']} |")
    out.append("")
out.append("## Adding a signal\n")
out.append("1. PLC programmer adds the register / bit.\n2. Add one entry to `config/tags.json` (category Alarm / Parameter / State / Process).\n3. Restart the application: the new tag is polled, alarms appear in the alarm list, parameters are audited. The SHA-256 of tags.json is recorded in the audit trail at start-up (change control).\n4. Re-run `build/tools/make_plc_doc.py` to refresh this document.\n")
open(os.path.join(root, "docs", "PLC_INTERFACE.md"), "w", encoding="utf-8").write("\n".join(out))
print("written", len(tags), "tags")
