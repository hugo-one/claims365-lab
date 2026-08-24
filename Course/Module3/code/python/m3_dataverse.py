"""Dataverse access for the fraud desk: sign in as yourself, read the book, write the referral.

DEMONSTRATION CODE - part of the Claims 365 training lab, written to be read rather than deployed.
See `m3_frauddesk.py` for what a production system would add.

TWO THINGS HERE ARE THE POINT OF THE MODULE, NOT PLUMBING

1. You sign in as YOU. No shared secret, no service account, so the agent's tools run with your
   permissions. "What can your agent see?" answers itself: exactly what its caller can see.
2. Everyone shares one claims book. It is read-only to you, and everything the desk writes is a new
   row stamped with your UPN, so nobody overwrites anybody.

Sections, in the order the desk uses them: config and http, sign-in, the book, the referral.
"""
from __future__ import annotations

import base64
import json
import os
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

import m3_env


# --------------------------------------------------------------------------- configuration
# Tenant and org live in `lab/.env` (see `m3_env.py`) because they are environment-specific.
# CLIENT_ID does not: it is the Azure CLI public client, a constant every tenant shares.
CLIENT_ID = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"        # Azure CLI public client

_target: tuple[str, str] | None = None


def target() -> tuple[str, str]:
    """`(tenant, org)`, resolved on FIRST USE rather than at import.

    Deliberately lazy. `m3_test.py` imports this module and never touches Dataverse - it is the
    offline suite the README tells you to run before anything else - so an unedited `lab/.env`
    must not stop it. The error still arrives, loudly and by name, the moment something actually
    needs a directory to sign in to.
    """
    global _target
    if _target is None:
        _target = m3_env.dataverse_target()
    return _target


def api() -> str:
    """The Web API base for the configured org."""
    return f"{target()[1]}/api/data/v9.2"


def __getattr__(name: str):
    """`TENANT`, `DATAVERSE` and `API` as module attributes, for `dv.DATAVERSE` and friends.

    Module-level __getattr__ (PEP 562) is consulted only for attribute access on the module, so
    code INSIDE this file calls target() / api() directly.
    """
    if name == "TENANT":
        return target()[0]
    if name == "DATAVERSE":
        return target()[1]
    if name == "API":
        return api()
    raise AttributeError(f"module {__name__!r} has no attribute {name!r}")

# Beside this file rather than in a home directory, so deleting the repo deletes the session.
TOKEN_CACHE = Path(__file__).resolve().parent / ".m3_token.json"

# Access tokens and expiries for the life of ONE process. The refresh token on disk is what
# survives - and it is ONE sign-in: the Dataverse token and the model-call token below are both
# redeemed from it, one per audience.
_mem: dict = {}


def _save_refresh_token(refresh_token: str) -> None:
    """Store the refresh token, readable only by you where the OS supports it.

    It is worth ~90 days of access as you. POSIX modes mean nothing on Windows, so failure is
    ignored there.
    """
    TOKEN_CACHE.write_text(json.dumps({"refresh_token": refresh_token}), encoding="utf-8")
    try:
        os.chmod(TOKEN_CACHE, 0o600)
    except (OSError, NotImplementedError):
        pass


# --------------------------------------------------------------------------- http
def _post_form(url: str, data: dict):
    """POST a form-encoded body to Entra and return `(status, parsed json)`.

    Errors are returned, not raised: while polling for a device code, `400 authorization_pending`
    is the normal case.
    """
    body = urllib.parse.urlencode(data).encode()
    req = urllib.request.Request(url, data=body, method="POST")
    req.add_header("Content-Type", "application/x-www-form-urlencoded")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, json.loads(r.read().decode())
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read().decode())


def device_login() -> str:
    """Interactive, once per Codespace. Prints a code you type into microsoft.com/devicelogin.

    Device code is the flow that works everywhere this lab runs: a Codespace has no browser to
    redirect to and cannot listen on localhost. `offline_access` earns the refresh token that makes
    every later command non-interactive.
    """
    s, dc = _post_form(
        f"https://login.microsoftonline.com/{target()[0]}/oauth2/v2.0/devicecode",
        {"client_id": CLIENT_ID, "scope": f"{target()[1]}/.default offline_access"})
    if "user_code" not in dc:
        raise SystemExit("could not start sign-in: " + json.dumps(dc)[:300])
    print("=" * 72)
    print("  SIGN IN AS YOURSELF")
    print("=" * 72)
    print(f"  1. open {dc['verification_uri']}")
    print(f"  2. enter code  {dc['user_code']}")
    print("  3. sign in with the Claims 365 account from your joining pack")
    print("=" * 72)
    # Poll until you finish in the browser. `interval` is the server's instruction: poll faster and
    # it answers `slow_down` instead of a token.
    deadline = time.time() + int(dc.get("expires_in", 900))
    while time.time() < deadline:
        time.sleep(int(dc.get("interval", 5)))
        s, tok = _post_form(
            f"https://login.microsoftonline.com/{target()[0]}/oauth2/v2.0/token",
            {"grant_type": "urn:ietf:params:oauth:grant-type:device_code",
             "client_id": CLIENT_ID, "device_code": dc["device_code"]})
        if s == 200:
            # Only the REFRESH token is stored; an access token would be stale by the next command.
            _save_refresh_token(tok["refresh_token"])
            print("signed in. token cached next to the code, and gitignored.")
            return tok["access_token"]
        if tok.get("error") != "authorization_pending":
            raise SystemExit("sign-in failed: " + tok.get("error_description", "")[:200])
    raise SystemExit("sign-in timed out")


def _redeem(resource: str) -> dict:
    """Exchange the cached refresh token for an access token scoped to `resource`.

    Shared by `token` (Dataverse) and `foundry_token` (model calls): ONE sign-in, redeemed once
    per audience. Entra rotates the refresh token on use, so the rotated one is saved each time.
    """
    if not TOKEN_CACHE.exists():
        raise SystemExit("not signed in. run:  python m3_login.py")
    rt = json.loads(TOKEN_CACHE.read_text(encoding="utf-8"))["refresh_token"]
    s, tok = _post_form(
        f"https://login.microsoftonline.com/{target()[0]}/oauth2/v2.0/token",
        {"grant_type": "refresh_token", "client_id": CLIENT_ID, "refresh_token": rt,
         "scope": f"{resource}/.default offline_access"})
    if s != 200:
        # A dead refresh token is unrecoverable, so delete it rather than fail the same way forever.
        TOKEN_CACHE.unlink(missing_ok=True)
        raise SystemExit("session expired. run:  python m3_login.py")
    if tok.get("refresh_token"):
        _save_refresh_token(tok["refresh_token"])
    return tok


def _jwt_claims(jwt: str) -> dict:
    """Decode a JWT payload WITHOUT verifying the signature - acceptable here, and not in a
    server. The token just came over TLS from Entra and is being read for a name and an expiry,
    not to make an access decision. The receiving service verifies it properly on every call."""
    payload = jwt.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))


def token() -> str:
    """A valid Dataverse token, refreshed from disk when this process holds none.

    Also decodes it to learn who you are. The UPN stamps every referral row, which is what keeps
    everyone's results apart - reading it from the token means it cannot be typed wrong or borrowed.
    """
    if "tok" in _mem and _mem["exp"] - 120 > time.time():
        return _mem["tok"]
    tok = _redeem(target()[1])
    claims = _jwt_claims(tok["access_token"])
    _mem["tok"], _mem["exp"] = tok["access_token"], claims["exp"]
    _mem["upn"] = claims.get("upn") or claims.get("unique_name") or claims.get("oid", "unknown")
    return _mem["tok"]


# The model endpoint's resource. A constant like CLIENT_ID, not config: it names Azure's
# Cognitive Services API surface and is the same value in every tenant.
FOUNDRY_RESOURCE = "https://cognitiveservices.azure.com"


def foundry_token() -> str:
    """A model-call token from the SAME sign-in, so nobody hands you a key.

    The refresh token `m3_login.py` cached is redeemed once for Dataverse and once for the model
    endpoint: your model calls are authorised - and attributable - as you, exactly like your
    data reads. A real FOUNDRY_KEY in lab/.env overrides this (see `m3_env.foundry`), which is
    also the route for running the lab against your own tenant.
    """
    if "ftok" in _mem and _mem["fexp"] - 120 > time.time():
        return _mem["ftok"]
    tok = _redeem(FOUNDRY_RESOURCE)
    _mem["ftok"], _mem["fexp"] = tok["access_token"], _jwt_claims(tok["access_token"])["exp"]
    return _mem["ftok"]


def whoami() -> str:
    """Your UPN, as Dataverse sees you. Signs in first if this process has not yet."""
    token()
    return _mem.get("upn", "unknown")


def _req(method: str, path: str, body=None):
    """One Dataverse Web API call, with the OData headers and the retry rule. `(status, json)`."""
    if "?" in path:
        # Encode what remains of the OData syntax - the spaces around `eq` and `or` - while
        # leaving the query structure ($, &, =) and anything `esc` already encoded (%) alone.
        # The space must NOT be in `safe`: an unencoded space reaches http.client as a control
        # character, and the exception names the URL but not the cause. String values were
        # percent-encoded by `esc` when the filter was built, so a raw & here is always a
        # parameter separator, never data.
        base, q = path.split("?", 1)
        path = base + "?" + urllib.parse.quote(q, safe="$&=(),'/*%")
    req = urllib.request.Request(f"{api()}/{path}",
                                 data=json.dumps(body).encode() if body is not None else None,
                                 method=method)
    req.add_header("Authorization", f"Bearer {token()}")
    req.add_header("Accept", "application/json")
    req.add_header("OData-MaxVersion", "4.0")
    req.add_header("OData-Version", "4.0")
    if body is not None:
        req.add_header("Content-Type", "application/json")
    # Retry transient failures: a hop fires a burst of short-lived connections and Dataverse
    # throttles a busy environment. Only a transient status or a connection that never landed - a
    # 400 or 403 comes straight back, since repeating it only delays the explanation.
    last = None
    for attempt in range(1, 5):
        try:
            with urllib.request.urlopen(req, timeout=90) as r:
                raw = r.read().decode()
                return r.status, (json.loads(raw) if raw else {})
        except urllib.error.HTTPError as e:
            raw = e.read().decode()
            if e.code in (429, 502, 503, 504) and attempt < 4:
                time.sleep(2 * attempt)
                continue
            return e.code, (json.loads(raw) if raw else {})
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            last = e
            if attempt < 4:
                time.sleep(2 * attempt)
                continue
    raise RuntimeError(
        f"Dataverse unreachable after 4 attempts: {last}. "
        f"If this persists, check your connection; the checkpoint is safe and re-running loses "
        f"nothing.")


def query(path: str) -> list[dict]:
    """GET an OData path and return the rows, raising on anything that is not a 200.

    Raising matters: a tool that swallowed a 403 would report "nobody is connected to this
    claimant", and a wrong answer that looks right is the worst outcome here.
    """
    s, r = _req("GET", path)
    if s != 200:
        raise RuntimeError(f"Dataverse read failed {s}: {str(r)[:300]}")
    return r.get("value", [])


def esc(v: str) -> str:
    """A string value made safe for an OData filter: quotes doubled, then percent-encoded.

    Two parsers see this value, and each has a character that breaks it. OData escapes a single
    quote by doubling it - a claimant called O'Neill would otherwise produce a syntax error that
    reads like a permissions problem. The URL parser splits on & - a repairer called
    Eccles Panel & Paint would otherwise truncate the query at the ampersand and fail as a
    malformed filter. Encoding here, once, covers both.
    """
    return urllib.parse.quote(str(v).replace("'", "''"), safe="")


# --------------------------------------------------------------------------- the book, read-only
# Named once so every query returns the same shape and adding a column is one edit.
CLAIM_COLS = ("cp_bookclaimref,cp_claimantref,cp_repairername,cp_claimtype,"
              "cp_amountclaimed,cp_claimstatus,cp_incidentdate,cp_incidentpostcode")
CLAIMANT_COLS = "cp_claimantref,cp_fullname,cp_email,cp_phone,cp_bankaccount,cp_postcode"


def get_claim(claim_ref: str) -> dict | None:
    """One claim by reference, or None. Intake uses it to refuse a claim that does not exist."""
    rows = query(f"cp_bookclaims?$select={CLAIM_COLS}"
                 f"&$filter=cp_bookclaimref eq '{esc(claim_ref)}'")
    return rows[0] if rows else None


def get_claimant(claimant_ref: str) -> dict | None:
    """One claimant by reference, or None. This is where the identifiers a ring shares live."""
    rows = query(f"cp_claimants?$select={CLAIMANT_COLS}"
                 f"&$filter=cp_claimantref eq '{esc(claimant_ref)}'")
    return rows[0] if rows else None


def claimants_sharing(attribute: str, value: str) -> list[dict]:
    """Everyone holding one exact identifier value. The single-value form of a hop.

    The attribute names are the agent-facing vocabulary, so a tool description never has to teach
    the model a Dataverse schema.
    """
    col = {"bank_account": "cp_bankaccount", "phone": "cp_phone",
           "postcode": "cp_postcode", "email": "cp_email"}[attribute]
    return query(f"cp_claimants?$select={CLAIMANT_COLS}&$filter={col} eq '{esc(value)}'")


def claims_of(claimant_ref: str) -> list[dict]:
    """Every claim belonging to one claimant. The exposure figure is summed from these rows."""
    return query(f"cp_bookclaims?$select={CLAIM_COLS}"
                 f"&$filter=cp_claimantref eq '{esc(claimant_ref)}'")


def claims_by_repairer(repairer: str) -> list[dict]:
    """Every claim at one repairer. Joined by NAME, so the book needs no relationship metadata."""
    return query(f"cp_bookclaims?$select={CLAIM_COLS}"
                 f"&$filter=cp_repairername eq '{esc(repairer)}'")


# --------------------------------------------------------------------------- batched reads
# A hop is a set operation: everyone who shares an identifier with anyone found so far. Doing it one
# person at a time costs a model round trip each, and round trips are what an agent's bill is made
# of. These do a whole hop in three queries however many people are in it.
# Far above anything this book can produce. It exists because these values are chosen by a MODEL,
# and asking about hundreds at once builds a URL past what Dataverse accepts, failing with an error
# that never mentions length.
MAX_FILTER_TERMS = 150


def _or_filter(column: str, values) -> str:
    """`col eq 'a' or col eq 'b' or ...`. Empty string when there is nothing to ask.

    Sorted and de-duplicated so the same hop always produces the same URL. Callers must treat "" as
    "ask nothing": a bare `$filter=` is a 400.
    """
    uniq = sorted({v for v in values if v})
    if len(uniq) > MAX_FILTER_TERMS:
        raise ValueError(
            f"asked about {len(uniq)} values in one query, more than the limit of "
            f"{MAX_FILTER_TERMS}. A filter that long exceeds the URL length Dataverse accepts, "
            f"and fails with an error that does not mention length.")
    return " or ".join(f"{column} eq '{esc(v)}'" for v in uniq)


def claimants_by_refs(refs) -> list[dict]:
    """Many claimants in one query. The first half of a hop: who are the people we already have?"""
    f = _or_filter("cp_claimantref", refs)
    if not f:
        return []
    return query(f"cp_claimants?$select={CLAIMANT_COLS}&$filter={f}")


def claimants_sharing_any(attribute: str, values) -> list[dict]:
    """Everyone holding ANY of these values. The second half of a hop.

    Bank account, phone and postcode only - the three the challenge step knows how to weigh.
    """
    col = {"bank_account": "cp_bankaccount", "phone": "cp_phone",
           "postcode": "cp_postcode"}[attribute]
    f = _or_filter(col, values)
    if not f:
        return []
    return query(f"cp_claimants?$select={CLAIMANT_COLS}&$filter={f}")


def claims_of_many(refs) -> list[dict]:
    """Every claim belonging to any of these claimants, in one query."""
    f = _or_filter("cp_claimantref", refs)
    if not f:
        return []
    return query(f"cp_bookclaims?$select={CLAIM_COLS}&$filter={f}")


# --------------------------------------------------------------------------- the referral, write
def new_run_id() -> str:
    """A UTC timestamp identifying one investigation. Scoped to one person by the referral ref."""
    return datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")


def write_referral(*, referral_ref, seed_claim, decision, ring_size, claim_count, exposure,
                   hops, approved_by, evidence, language, run_id) -> bool:
    """Write the referral header: one row per person per run.

    Keyword-only on purpose: eleven positional arguments, several of them integers, is the shape
    where a transposition silently writes `ring_size` into `claim_count`. `cp_investigator` comes
    from `whoami()`, so the row records who actually ran it.
    """
    s, r = _req("POST", "cp_fraudreferrals", {
        "cp_referralref": referral_ref,
        "cp_seedclaimref": seed_claim,
        "cp_decision": decision,
        "cp_ringsize": int(ring_size),
        "cp_claimcount": int(claim_count),
        "cp_exposure": float(exposure),
        "cp_hops": int(hops),
        "cp_investigator": whoami(),
        "cp_approvedby": approved_by,
        "cp_language": language,
        "cp_runid": run_id,
        "cp_evidence": evidence[:3999],
    })
    if not (200 <= s < 300):
        raise RuntimeError(f"referral write failed {s}: {str(r)[:300]}")
    return True


def write_referral_claim(*, referral_ref, claim_ref, claimant_ref, reason, strength) -> None:
    """One evidence line: this claim is in this referral, and why.

    These make the referral auditable rather than a summary row with a number on it.
    """
    s, r = _req("POST", "cp_referralclaims", {
        "cp_referralclaimref": f"{referral_ref}|{claim_ref}"[:139],
        "cp_referralref": referral_ref,
        "cp_bookclaimref": claim_ref,
        "cp_claimantref": claimant_ref,
        "cp_linkreason": reason[:199],
        "cp_linkstrength": strength,
    })
    if not (200 <= s < 300):
        raise RuntimeError(f"referral line write failed {s}: {str(r)[:300]}")


def my_referrals() -> list[dict]:
    """Every referral YOU wrote, newest first.

    The payoff of stamping the UPN: "mine" is a filter rather than a race. Everyone can read
    everyone's rows and nobody can change them.
    """
    me = esc(whoami())
    return query("cp_fraudreferrals?$select=cp_referralref,cp_decision,cp_ringsize,"
                 "cp_claimcount,cp_exposure,cp_hops,cp_approvedby,cp_language,cp_runid"
                 f"&$filter=cp_investigator eq '{me}'&$orderby=createdon desc")
