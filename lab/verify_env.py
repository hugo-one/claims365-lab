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


# The FOUNDRY_KEY value that ships in .env.sample. While it is still exactly this, the file has
# not been filled in yet - which before the course is the NORMAL state, not a mistake: the
# Codespace creates lab/.env from the sample, and the key is given out at the start of Module 3.
PLACEHOLDER_KEY = "<ask-your-instructor-or-paste-an-entra-token>"


def check_env_file():
    """Shape-check lab/.env without any network call.

    Returns None when the file does not exist, else `(problems, waiting_for_key)`.
    `waiting_for_key` is True while FOUNDRY_KEY still holds the untouched sample value.
    Everything else is checked as usual, catching the mistakes that otherwise surface for
    the first time on the day: quotes around values, an edited-but-broken placeholder, an
    endpoint missing /openai/v1/.
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
    waiting_for_key = vals.get("FOUNDRY_KEY", "") == PLACEHOLDER_KEY
    # The three values the labs actually read.
    # FOUNDRY_ENDPOINT also appears in .env.sample but no lab code reads it, so it is not checked.
    for key in ("FOUNDRY_OPENAI_V1", "MODEL_DEPLOYMENT", "FOUNDRY_KEY"):
        v = vals.get(key, "")
        if key == "FOUNDRY_KEY" and waiting_for_key:
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
    return problems, waiting_for_key

REQUIRED = [
    ("agent_framework", "agent-framework", "Module 3 - the workflow engine"),
    ("openai", "openai", "Modules 2 and 3 - every model call"),
    ("azure.identity", "azure-identity", "keyless auth option"),
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
env_problems, waiting_for_key = (None, False) if checked is None else checked

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
    print("  then edit lab/.env and run this check again.")
    print()
    print("  Already filled your values in and still seeing this? You probably")
    print("  edited .env.sample - the near-identical file next door. Open lab/.env")
    print("  and put your values there.")
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

if waiting_for_key:
    # The Codespace and the dev container create lab/.env from the sample, so before the course
    # this is the state nearly everybody is in. It is the right one, so it exits 0.
    print("  PACKAGES READY - waiting for the model key")
    print("=" * 70)
    print("  Everything is in place except FOUNDRY_KEY, which still holds the")
    print("  sample's placeholder. That is the right state before the course:")
    print("  your instructor gives the key out at the start of Module 3. Paste it")
    print("  over the placeholder then, run this check again, and it will say")
    print("  ENVIRONMENT READY.")
    print()
    print("  Building in your own tenant instead? Your own resource key, or an")
    print("  Entra token (see lab/.env.sample), goes in the same slot now.")
    sys.exit(0)

print("  Packages installed, and lab/.env is filled in and well-formed.")
print("  Next: cd Course/Module3/code/python && python m3_test.py   (offline, no credentials)")
print("=" * 70)
print("  ENVIRONMENT READY")
sys.exit(0)
