# MergeVerifier

Local diagnostic console tool for verifying a completed counterparty merge. It only calls the
read-only Egress endpoint; it never reads or accepts a MoySklad access token.

Create `tools/MergeVerifier/.env` from the committed template:

```bash
cp tools/MergeVerifier/.env.example tools/MergeVerifier/.env
```

Then replace the placeholder values in `.env`:

```bash
MERGE_VERIFIER_INTERNAL_API_KEY=...
MERGE_VERIFIER_ACCOUNT_ID=ACCOUNT_ID
MERGE_VERIFIER_MAIN_KA=MAIN_ID
MERGE_VERIFIER_DUPLICATE_IDS=DUPLICATE_ID_1,DUPLICATE_ID_2
MERGE_VERIFIER_EGRESS_URL=http://localhost:5024/
```

The tool loads this file automatically. Values already exported in the process environment
take precedence over `.env`.

Capture the baseline, run Merge through the usual application, then verify it:

```bash
dotnet run --project tools/MergeVerifier -- capture
dotnet run --project tools/MergeVerifier -- verify --snapshot snapshots/20260919-153015-main_XXXXXXXX/before.json
```

`capture` reads the account and counterparty IDs from `.env`. `verify` does not need
`--account-id`, `--main`, or `--duplicates`: it reads the account and counterparty IDs from
`before.json`, captures the current state, and compares it with the baseline.

`capture` writes `before.json`; `verify` writes sibling `after.json` and `report.json`. Exit codes:
`0` passed, `1` document verification failed, `2` verifier/input/network error.
