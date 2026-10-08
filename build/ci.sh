#!/usr/bin/env bash
# Linux CI: builds the platform-independent projects, runs all tests, compile-checks the WPF + Cognex
# sources against reference assemblies (build/uicheck/refs) and regenerates generated docs.
# Usage: build/ci.sh [output-dir]
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="${1:-artifacts}"
mkdir -p "$OUT"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

echo "== generate language files and PLC doc"
python3 build/tools/make_lang.py
python3 build/tools/make_plc_doc.py

echo "== build core"
for p in src/CheckBarcode.Domain src/CheckBarcode.Application src/CheckBarcode.Infrastructure \
         src/CheckBarcode.Simulation tools/CheckBarcode.PlcSimulator tests/CheckBarcode.Tests; do
  dotnet build "$p" -c Release -warnaserror --nologo -v q
done

echo "== tests"
dotnet run --project tests/CheckBarcode.Tests -c Release --no-build -- --trace "$OUT/trace.md" | tee "$OUT/test-results.txt"
grep -q " 0 failed" "$OUT/test-results.txt"
python3 build/tools/make_trace.py "$OUT/trace.md"

echo "== WPF / Cognex compile check"
dotnet build build/uicheck -c Release --nologo -v q | tee "$OUT/uicheck.txt"
grep -q "Build succeeded" "$OUT/uicheck.txt"

echo "== PLC simulator package"
dotnet publish tools/CheckBarcode.PlcSimulator -c Release -o "$OUT/PlcSimulator" --nologo -v q
echo "CI OK -> $OUT"
