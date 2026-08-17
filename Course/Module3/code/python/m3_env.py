"""Configuration for the fraud desk, read from `lab/.env`. The Python twin of `Env.cs`.

DEMONSTRATION CODE - part of the Claims 365 training lab, written to be read rather than deployed.

Only `FOUNDRY_KEY` is a secret. The endpoint, model, tenant and Dataverse org say *where* things
live and grant nothing on their own, so they ship pre-filled and you paste one value.
"""
import os
from pathlib import Path

# The course environment. Defaults, so the lab works before you edit anything.
DEFAULT_TENANT = "30d4eca5-0b84-4868-89d5-b0fc78c105c4"
DEFAULT_ORG = "https://org3425656b.crm4.dynamics.com"

_cache: dict | None = None


def lab_env() -> dict:
    """Parse `lab/.env`, searching upward from this file rather than the working directory.

    Returns an empty dict when there is no file; only `foundry()` treats that as fatal.
    """
    global _cache
    if _cache is not None:
        return _cache
    _cache = {}
    here = Path(__file__).resolve()
    for parent in [here.parent, *here.parents]:
        for candidate in (parent / "lab" / ".env", parent / ".env"):
            if candidate.is_file():
                for line in candidate.read_text(encoding="utf-8").splitlines():
                    line = line.strip()
                    if line and not line.startswith("#") and "=" in line:
                        k, v = line.split("=", 1)
                        _cache[k.strip()] = v.strip().strip('"')
                return _cache
    return _cache


def setting(name: str, default: str = "") -> str:
    """Process environment first, then `lab/.env`, then the default."""
    return os.environ.get(name) or lab_env().get(name) or default


def dataverse_target() -> tuple[str, str]:
    """`(tenant, org)`. Never raises, because both have a working default.

    The trailing slash is stripped: a pasted URL often carries one, and `{org}//api/data/v9.2`
    404s without mentioning the extra slash.
    """
    return setting("DATAVERSE_TENANT", DEFAULT_TENANT), \
        setting("DATAVERSE_ORG", DEFAULT_ORG).rstrip("/")


REQUIRED_FOUNDRY = ("FOUNDRY_OPENAI_V1", "FOUNDRY_KEY", "MODEL_DEPLOYMENT")


def foundry() -> dict:
    """The Foundry endpoint, key and deployment. Raises if any is missing.

    No default for a key: guessing turns an unfinished setup step into an unexplained 401 later.
    """
    missing = [n for n in REQUIRED_FOUNDRY if not setting(n)]
    if missing:
        raise SystemExit(
            "lab/.env is missing " + ", ".join(missing) + ".\n"
            "  From the repository root:  cp lab/.env.sample lab/.env\n"
            "  then paste the FOUNDRY_KEY your instructor gives out at the start of Module 3. "
            "Everything else is already filled in.")
    return {n: setting(n) for n in REQUIRED_FOUNDRY}
