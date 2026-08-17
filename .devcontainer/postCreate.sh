#!/usr/bin/env bash
# Runs once when your Codespace or dev container is created: installs the lab's Python
# dependencies and smoke-tests them.
#
# It lives at the REPO ROOT, not under lab/. Codespaces only looks for a dev container at the
# repository root, and the labs need Course/ as well as lab/, so every path below is root-relative.
set -uo pipefail        # deliberately NOT -e; an install failure is handled explicitly below

echo "== Installing Claims 365 lab dependencies (pinned) =="
python -m pip install --upgrade pip

# Install the LOCK rather than requirements.txt. agent-framework pulls in about thirty sub-packages
# that carry no version specifiers, so two fresh installs days apart can differ on dozens of them.
# The lock is the exact set these labs were tested against.
#
# The failure is handled here rather than by `set -e` because Codespaces opens the editor even when
# this script fails. A silent abort would leave you with a container that looks healthy until the
# first import fails much later. Instead: fall back to the readable requirements, and write a file
# into the explorer that is hard to miss.
if ! python -m pip install -r lab/requirements.lock.txt; then
  echo
  echo "############################################################################"
  echo "##  THE PINNED INSTALL FAILED. Falling back to lab/requirements.txt.       ##"
  echo "##  Versions may differ from the tested set - tell your instructor.        ##"
  echo "############################################################################"
  echo
  if ! python -m pip install -r lab/requirements.txt; then
    echo "SETUP FAILED - neither the lock nor the plain requirements installed." > SETUP-FAILED.txt
    echo "Run this and send your instructor the output:" >> SETUP-FAILED.txt
    echo "  python -m pip install -r lab/requirements.txt" >> SETUP-FAILED.txt
    echo
    echo "SETUP FAILED. See SETUP-FAILED.txt in the file explorer."
    exit 1
  fi
  echo "FELL BACK to lab/requirements.txt - versions are not the tested set." > SETUP-USED-FALLBACK.txt
fi

# Pin VS Code to the interpreter we just installed into. The Run button otherwise defaults to
# Debian's /usr/bin/python3 in this image, which has none of these packages, and every lab script
# fails with ModuleNotFoundError. Detected rather than hard-coded so it survives an image change.
PYBIN="$(command -v python)"
mkdir -p .vscode
cat > .vscode/settings.json <<EOF
{
  "python.defaultInterpreterPath": "$PYBIN",
  "python.terminal.activateEnvInCurrentTerminal": true
}
EOF
echo "== VS Code pinned to $PYBIN =="

echo "== Smoke test: imports + versions =="
python - <<'PY'
import importlib.metadata as m
import agent_framework, openai, azure.identity, PIL
print("  python         :", __import__("sys").version.split()[0])
print("  agent-framework:", m.version("agent-framework"))
print("  openai         :", openai.__version__)
print("  azure-identity :", m.version("azure-identity"))
from agent_framework.openai import OpenAIChatClient          # Azure OpenAI client path
from agent_framework.orchestrations import SequentialBuilder  # orchestration builders
from agent_framework import FileCheckpointStorage, tool, create_always_approve_tool_response
print("  Agent Framework symbols resolve: OpenAIChatClient, SequentialBuilder, FileCheckpointStorage, tool")

# Module 2 Part 6 imports opentelemetry.sdk UNCONDITIONALLY. If it is missing, that part dies with
# ModuleNotFoundError halfway through the lab - so fail loudly here instead, while there is time.
from opentelemetry.sdk.trace import TracerProvider
print("  opentelemetry-sdk        :", m.version("opentelemetry-sdk"))
import azure.monitor.opentelemetry
print("  azure-monitor-opentelemetry:", m.version("azure-monitor-opentelemetry"))
print("  All imports the labs need are present.")
PY

# Create the attendee's .env from the sample. -n never overwrites, so a rebuild
# or re-run cannot clobber a file someone has already filled in.
cp -n lab/.env.sample lab/.env 2>/dev/null || true

echo
echo "== Next steps =="
echo "  1) lab/.env was just created from lab/.env.sample. The endpoint and model lines are"
echo "     pre-filled for the course environment and need no editing."
echo "  2) Only FOUNDRY_KEY needs a value, and your instructor gives it out at the start of"
echo "     Module 3 - until then the placeholder is fine where it is. Two options when the"
echo "     time comes (delete the <placeholder>, brackets included):"
echo "       a) paste the key your instructor gives you, or"
echo "       b) KEYLESS - use your own Entra token instead, no code change needed:"
echo "            az login --use-device-code"
echo "            az account get-access-token --resource https://cognitiveservices.azure.com \\"
echo "              --query accessToken -o tsv"
echo "          Paste that value as FOUNDRY_KEY. The OpenAI SDK sends it as a bearer token."
echo "          NOTE: it expires after ~86 minutes, so you may need to repeat this once."
echo "  3) python lab/verify_env.py                            # offline, no credentials"
echo "  4) cd Course/Module3/code/python && python m3_test.py  # offline, no model calls"
echo
echo "Lab environment ready."
echo "  Work from the guides your instructor sent you, not from this repository:"
echo "    Module 1  browser only, nothing to run here"
echo "    Module 2  browser only - the schema to paste and the evidence to attach"
echo "    Module 3  Course/Module3/code/python/  (Python)  or  .../dotnet/  (C#)"
