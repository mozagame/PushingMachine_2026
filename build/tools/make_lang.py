"""Generates config/lang/vi.json and en.json from lang_core.py (+ lang_ui.py when present)."""
import json, os, sys
here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, here)
from lang_core import CORE
table = dict(CORE)
try:
    from lang_ui import UI
    dup = set(UI) & set(CORE)
    if dup: raise SystemExit(f"duplicate keys: {dup}")
    table.update(UI)
except ImportError:
    pass
out = os.path.join(here, "..", "..", "config", "lang")
os.makedirs(out, exist_ok=True)
for i, lang in enumerate(["vi", "en"]):
    with open(os.path.join(out, lang + ".json"), "w", encoding="utf-8") as f:
        json.dump({k: v[i] for k, v in sorted(table.items())}, f, ensure_ascii=False, indent=1)
print(len(table), "keys")
