# MergeVerifier

Local diagnostic console tool for verifying a completed counterparty merge. It only calls the
read-only Egress endpoint; it never reads or accepts a MoySklad access token.

Set the Egress connection and merge scope before capture:

```bash
export MERGE_VERIFIER_INTERNAL_API_KEY='...'
export MERGE_VERIFIER_MAIN_KA='MAIN_ID'
export MERGE_VERIFIER_DUPLICATE_IDS='DUPLICATE_ID_1,DUPLICATE_ID_2'
# Optional when Egress is not at localhost:5012
export MERGE_VERIFIER_EGRESS_URL='http://localhost:5012/'
```

Capture the baseline, run Merge through the usual application, then verify it:

```bash
dotnet run --project tools/MergeVerifier -- capture --account-id ACCOUNT_ID
dotnet run --project tools/MergeVerifier -- verify --snapshot snapshots/20260919-153015-main_XXXXXXXX/before.json
```

`capture` writes `before.json`; `verify` writes sibling `after.json` and `report.json`. Exit codes:
`0` passed, `1` document verification failed, `2` verifier/input/network error.
