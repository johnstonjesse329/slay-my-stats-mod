"""Check that every Harmony patch in this mod still fits the game's assembly.

Harmony binds a patch's parameters **by name**, and gets no help from the
compiler: if a game update renames a parameter, the patch silently matches
nothing and the event it was meant to record simply never appears. This tool
compares the source against `sts2.dll` and fails loudly, so that a renamed
parameter is a red build instead of a run's worth of playtime.

    python tools/check_patches.py

Reads SlayMyStatsCode/*.cs, reflects the game's assembly with PowerShell (nothing
is launched), and prints one line per patch.
"""
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CODE = ROOT / "SlayMyStatsCode"

# Where the game keeps its assembly, when the msbuild props cannot be asked.
DEFAULT_DLLS = [
    Path(r"E:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64\sts2.dll"),
]

TYPE_MAP = {
    "int": "Int32", "bool": "Boolean", "decimal": "Decimal", "float": "Single",
    "double": "Double", "string": "String", "long": "Int64",
}

PATCH_RE = re.compile(
    r"\[HarmonyPatch\(typeof\((?P<type>\w+)\),\s*nameof\((?P<holder>\w+)\.(?P<method>\w+)\)\)\]"
    r"(?P<body>.*?)(?=\[HarmonyPatch|\Z)",
    re.S,
)
POSTFIX_RE = re.compile(
    r"\[Harmony(?:Post|Pre)fix[^\]]*\]\s*public\s+static\s+\w+\s+\w+\s*\((?P<params>[^)]*)\)",
    re.S,
)


def dll_path() -> Path:
    for p in DEFAULT_DLLS:
        if p.exists():
            return p
    raise SystemExit("sts2.dll not found; set DEFAULT_DLLS in this script.")


def shell() -> str:
    """PowerShell 7: the game's assembly targets .NET 9, which the in-box
    powershell.exe (5.1, .NET Framework) cannot even load."""
    for name in ("pwsh", "powershell"):
        if subprocess.run(["where", name], capture_output=True).returncode == 0:
            if name == "powershell":
                raise SystemExit("pwsh (PowerShell 7) is required to read a .NET 9 assembly.")
            return name
    raise SystemExit("pwsh not found")


def signatures(dll: Path) -> dict:
    """{method name: [[type, name], ...]} straight from the assembly."""
    ps = f"""
$asm = [System.Reflection.Assembly]::LoadFrom('{dll}')
$t = $asm.GetType('MegaCrit.Sts2.Core.Hooks.Hook')
foreach ($m in $t.GetMethods()) {{
  if ($m.DeclaringType.FullName -ne 'MegaCrit.Sts2.Core.Hooks.Hook') {{ continue }}
  $ps = @($m.GetParameters() | ForEach-Object {{ [pscustomobject]@{{ t = $_.ParameterType.Name; n = $_.Name }} }})
  [pscustomobject]@{{ name = $m.Name; ps = $ps }} | ConvertTo-Json -Compress -Depth 4
}}
"""
    out = subprocess.run(
        [shell(), "-NoProfile", "-Command", ps],
        capture_output=True, text=True, check=True,
    ).stdout
    found = {}
    for line in out.splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        row = json.loads(line)
        found.setdefault(row["name"], []).append([[p["t"], p["n"]] for p in row["ps"]])
    return found


def patches():
    """[(target, [(param type, name), ...], source file, line)] for everything patched."""
    out = []
    for f in sorted(CODE.glob("*.cs")):
        text = f.read_text(encoding="utf-8")
        for m in PATCH_RE.finditer(text):
            post = POSTFIX_RE.search(m.group("body"))
            if not post:
                continue
            params = []
            for chunk in post.group("params").split(","):
                parts = chunk.split()
                if len(parts) < 2 or parts[-1].startswith("__"):
                    continue
                params.append((parts[-2], parts[-1]))
            line = text[: m.start()].count("\n") + 1
            out.append((m.group("method"), m.group("holder"), params, f.name, line))
    return out


def main() -> int:
    real = signatures(dll_path())
    bad = 0
    checked = 0
    skipped = 0
    for target, holder, params, file, line in patches():
        if holder != "Hook":
            skipped += 1  # not one of the game's event points; not this tool's business
            continue
        sigs = real.get(target)
        if not sigs:
            print(f"FAIL {file}:{line} {target} does not exist on Hook")
            bad += 1
            continue
        matched, missing_all = None, None
        for sig in sigs:
            names = {n for _, n in sig}
            missing = [n for _, n in params if n not in names]
            if not missing:
                matched = sig
                break
            if missing_all is None:
                missing_all = missing
        if matched is None:
            print(f"FAIL {file}:{line} {target}: no such parameter(s) {missing_all}")
            bad += 1
            continue
        kinds = {n: t for t, n in matched}
        for t, n in params:
            got, want = TYPE_MAP.get(t, t), kinds.get(n, "")
            if got != want and not want.startswith("IReadOnlyList") and not want.startswith("IEnumerable"):
                print(f"FAIL {file}:{line} {target}: {n} is {want}, patch says {got}")
                bad += 1
        checked += 1
    print(f"{checked} patch(es) checked against {dll_path().name}, {bad} problem(s)"
          + (f", {skipped} skipped (not a Hook)" if skipped else ""))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
