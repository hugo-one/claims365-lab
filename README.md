# Claims 365 — lab code

The code you run during **Build Production-Ready AI Agents with the Microsoft Stack**.

**The step-by-step guides are not in this repository.** They are sent to you separately before the
day, as self-contained pages you can open offline. Work from those. This repository holds only the
files they tell you to use.

**Only Module 3 asks you to run code.** Modules 1 and 2 are built entirely in the browser.

## Setup

1. `cp lab/.env.sample lab/.env` — in a Codespace or dev container the copy and the dependency
   install have already been done for you. You still have to do step 2.
2. **Edit `lab/.env`: four values are `<placeholders>` and the lab will not start until you
   replace them** — `FOUNDRY_ENDPOINT`, `FOUNDRY_OPENAI_V1`, `DATAVERSE_TENANT` and
   `DATAVERSE_ORG`. The file says where each comes from, and so does *Set up your own tenant*
   in your course materials. Nothing guesses on your behalf: a half-edited file stops with the
   line named, rather than signing you in to a directory you are not a member of.

   **There is still no key to paste.** Model calls are keyless — they reuse the sign-in from
   Module 3's first step (`python m3_login.py`), the same session that reads the claims book.
   Leave `FOUNDRY_KEY` exactly as it is unless you want to override that.
3. `python lab/verify_env.py` — it should end `ENVIRONMENT READY`.

## What is here

| Path | |
|---|---|
| `lab/` | Pinned requirements, `.env.sample`, and `verify_env.py` |
| `Course/Module3/code/python/` and `.../dotnet/` | **Module 3**, Microsoft Agent Framework: the workflow, the CLI and the tests, in Python and C#. The only code you run |
| `Course/Module2/evidence/` | **Module 2**: the damage photographs and repair quotes you attach in the browser. The photographs are real and openly licensed — attribution in `CREDITS.md`, which must stay with them |
| `Course/Module2/code/claim_assessment.schema.json` | **Module 2**: the response schema you paste into your agent. The same file ships in the tenant setup pack |
| `.devcontainer/` | The Codespace definition |

**Module 1** is browser only and needs nothing from here.

**Module 2** is browser only too. You build the assessor in the Microsoft Foundry portal, paste the
schema above into it, attach two of the evidence files, and then submit a claim on the Contoso
website. You will not open a terminal for it.

## Prove it works, offline and free

```bash
python lab/verify_env.py                              # no credentials needed
cd Course/Module3/code/python && python m3_test.py           # offline, no model calls
```

---

Claims 365 and Contoso Insurance are training fiction. Every policy, claim, supplier and figure is
invented. The damage photographs are real and openly licensed; see
`Course/Module2/evidence/CREDITS.md`.
