#!/usr/bin/env python3
"""Prove this machine can run the labs. No credentials, no network, no model calls.

Run it in the Codespace, or locally, whenever something looks wrong:

    python lab/verify_env.py

It answers the question the Codespace cannot: did the dependency install actually SUCCEED?
Codespaces opens the editor even when postCreate.sh fails, so a broken container looks identical
to a working one until the first import blows up mid-lab.
"""
import importlib.metadata as md
import platform
import sys
from pathlib import Path


# FOUNDRY_KEY values that mean KEYLESS - the sample's placeholder (current and previous wording),
# or nothing at all. This is the normal, final state: model calls reuse the Module 3 sign-in
# (python m3_login.py), so there is no key to paste. A real value overrides the sign-in.
KEYLESS_VALUES = {"",
                  "<leave-as-is-for-keyless-or-paste-your-own>",
                  "<ask-your-instructor-or-paste-an-entra-token>"}


def check_env_file():
    """Shape-check lab/.env without any network call.

    Returns None when the file does not exist, else `(problems, keyless)`.
    `keyless` is True while FOUNDRY_KEY is empty or still the sample's placeholder - the
    normal state, since model calls reuse the Module 3 sign-in. Everything else is checked
    as usual, catching the mistakes that otherwise surface for the first time on the day:
    quotes around values, an edited-but-broken placeholder, an endpoint missing /openai/v1/.
    """
    p = Path(__file__).resolve().parent / ".env"
    if not p.exists():
        return None
    vals = {}
    for raw in p.read_text(encoding="utf-8", errors="replace").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        k, _, v = line.partition("=")
        vals[k.strip()] = v.strip()
    problems = []
    keyless = vals.get("FOUNDRY_KEY", "") in KEYLESS_VALUES
    # The values the labs actually read. FOUNDRY_KEY is optional (keyless is the normal state);
    # FOUNDRY_ENDPOINT also appears in .env.sample but no lab code reads it, so it is not checked.
    # Everything else must be YOUR value - the sample ships <placeholders>, not defaults.
    # DATAVERSE_TENANT and DATAVERSE_ORG are checked too: m3_env.dataverse_target() refuses to
    # guess them, so a file that still holds their placeholders fails at Module 3's first
    # command. Catching it here means the reader learns it from the environment check instead.
    for key in ("FOUNDRY_OPENAI_V1", "MODEL_DEPLOYMENT", "FOUNDRY_KEY",
                "DATAVERSE_TENANT", "DATAVERSE_ORG"):
        v = vals.get(key, "")
        if key == "FOUNDRY_KEY" and keyless:
            continue
        if not v:
            problems.append(f"{key} is empty or missing - it should have a value")
        elif v[0] in "\"'" or v[-1] in "\"'":
            problems.append(f"{key} is wrapped in quotation marks - remove them, quotes break the call")
        elif "<" in v or ">" in v:
            problems.append(f"{key} still holds the <placeholder> - delete it, angle brackets included, "
                            "and paste the real value")
    v1 = vals.get("FOUNDRY_OPENAI_V1", "")
    if (v1 and "<" not in v1 and v1[0] not in "\"'" and v1[-1] not in "\"'"
            and not v1.endswith("/openai/v1/")):
        problems.append("FOUNDRY_OPENAI_V1 must end /openai/v1/ - including the final slash")
    return problems, keyless

REQUIRED = [
    ("agent_framework", "agent-framework", "Module 3 - the workflow engine"),
    ("openai", "openai", "Modules 2 and 3 - every model call"),
    ("azure.identity", "azure-identity", "own-tenant auth option"),
    ("PIL", "pillow", "Module 2 - evidence images"),
    ("opentelemetry.sdk", "opentelemetry-sdk", "Module 2 - observability"),
    ("azure.monitor.opentelemetry", "azure-monitor-opentelemetry", "Module 2 - observability"),
]

SYMBOLS = [
    ("agent_framework", ["Executor", "WorkflowBuilder", "FileCheckpointStorage", "handler",
                         "response_handler", "tool"]),
]

# The floor the guides promise, and the version the Codespace actually ships. Anything at or above
# the floor is supported; only the Codespace figure is worth mentioning, and only as a note.
MIN_PYTHON = (3, 12)
CODESPACE_PYTHON = (3, 13)

bad = []

print("=" * 70)
print("  Claims 365 - environment check")
print("=" * 70)
print(f"  python      : {sys.version.split()[0]}   ({sys.executable})")
print(f"  platform    : {platform.system()} {platform.machine()}")
if sys.version_info[:2] < MIN_PYTHON:
    print(f"  FAIL python  {sys.version_info.major}.{sys.version_info.minor} is below the "
          f"supported minimum {MIN_PYTHON[0]}.{MIN_PYTHON[1]}")
    bad.append("python")
elif sys.version_info[:2] != CODESPACE_PYTHON:
    print(f"  note   the Codespace uses {CODESPACE_PYTHON[0]}.{CODESPACE_PYTHON[1]}; "
          f"{sys.version_info.major}.{sys.version_info.minor} is supported too")
print()

for mod, dist, why in REQUIRED:
    try:
        __import__(mod)
        try:
            ver = md.version(dist)
        except md.PackageNotFoundError:
            ver = "installed, no metadata"
        print(f"  OK   {dist:32} {ver:22} {why}")
    except Exception as e:                                  # noqa: BLE001
        print(f"  FAIL {dist:32} {'-':22} {why}")
        print(f"         {type(e).__name__}: {e}")
        bad.append(dist)

print()
for mod, names in SYMBOLS:
    try:
        m = __import__(mod)
        missing = [n for n in names if not hasattr(m, n)]
        if missing:
            print(f"  FAIL {mod}: missing {', '.join(missing)}")
            bad.append(mod)
        else:
            print(f"  OK   {mod} exposes {', '.join(names)}")
    except Exception:
        pass                                                # already reported above

try:
    from agent_framework.openai import OpenAIChatClient     # noqa: F401
    print("  OK   agent_framework.openai.OpenAIChatClient  (Module 3 Part 8)")
except Exception as e:                                      # noqa: BLE001
    print(f"  FAIL agent_framework.openai: {type(e).__name__}: {e}")
    bad.append("agent-framework-openai")

checked = check_env_file()
env_problems, keyless = (None, False) if checked is None else checked

print()
print("=" * 70)
if bad:
    print("  ENVIRONMENT IS NOT READY")
    print("=" * 70)
    print("  Missing: " + ", ".join(sorted(set(bad))))
    print()
    print("  Fix it with:")
    print("      python -m pip install -r lab/requirements.lock.txt")
    print()
    print("  If that fails, send your instructor the error. Do not carry on -")
    print("  the labs will fail later with a less obvious message.")
    sys.exit(1)

if env_problems is None:
    # Legitimate on the local route; the .env step simply has not happened yet.
    print("  PACKAGES READY - lab/.env not created yet")
    print("=" * 70)
    print("  Every package the labs need is importable.")
    print("  When you reach the .env step of your prep page, create the file with")
    print("      cp lab/.env.sample lab/.env                    (macOS / Linux)")
    print("      Copy-Item lab/.env.sample lab/.env             (Windows PowerShell)")
    print("  and run this check again - the sample needs no editing.")
    print()
    print("  Did that step and still seeing this? You probably edited .env.sample -")
    print("  the near-identical file next door - instead of copying it to lab/.env.")
    print("  Run the copy command above.")
    sys.exit(0)

if env_problems:
    print("  ENVIRONMENT IS NOT READY - lab/.env needs attention")
    print("=" * 70)
    for prob in env_problems:
        print(f"  - {prob}")
    print()
    print("  Open lab/.env, fix the lines above, save with Ctrl+S, and run this")
    print("  check again. (Values are checked for shape only - nothing is sent")
    print("  anywhere.)")
    sys.exit(1)

if keyless:
    # The Codespace and the dev container create lab/.env from the sample, and with no key the
    # labs are KEYLESS - for nearly everybody this is the final state, not a waiting room.
    print("  Packages installed, and lab/.env needs nothing from you.")
    print("  Model calls are KEYLESS: they reuse the sign-in from Module 3's first")
    print("  step (python m3_login.py), the same session that reads the claims book.")
    print()
    print("  Building in your own tenant instead? Your own resource key, or an")
    print("  Entra token (see lab/.env.sample), goes in the FOUNDRY_KEY slot and")
    print("  overrides the sign-in.")
    print("=" * 70)
    print("  ENVIRONMENT READY")
    sys.exit(0)

print("  Packages installed. lab/.env carries a real FOUNDRY_KEY, which overrides")
print("  the keyless sign-in - model calls will use the key.")
print("  Next: cd Course/Module3/code/python && python m3_test.py   (offline, no credentials)")
print("=" * 70)
print("  ENVIRONMENT READY")
sys.exit(0)
