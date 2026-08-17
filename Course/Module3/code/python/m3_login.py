"""Sign in to Dataverse as yourself. Run once per Codespace.

DEMONSTRATION CODE - part of the Claims 365 training lab.

No shared secret, no service account: the agent's tools run with YOUR permissions, which is why
you can read the claims book and cannot edit it.

    python m3_login.py
"""
import m3_dataverse as dv

# A postcode the book uses, so this proves the sign-in reached DATAVERSE and not merely Entra: a
# token can be issued perfectly and still read nothing if the role grant is missing.
TEST_POSTCODE = "M20 4WX"


def main() -> None:
    dv.device_login()
    print(f"\nyou are {dv.whoami()}")
    n = len(dv.claimants_sharing("postcode", TEST_POSTCODE))
    print(f"claims book reachable: {n} claimant(s) at the test postcode")
    print("\nnext:  python m3_verify_book.py")


# Guarded, so importing this file does not sign you in as a side effect.
if __name__ == "__main__":
    main()
