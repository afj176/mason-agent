#!/bin/bash
# Makes the small project the stand-in model's demo session works on, for screenshots of the apps.
#   demo-project.sh FOLDER
# Then, with Tests/stub-model.py running, ask Mason in that folder about "the importer".
set -euo pipefail
folder="${1:?Give the folder to create.}"
mkdir -p "$folder"
cat > "$folder/importer.py" <<'PY'
def parse_rows(lines):
    rows = []
    for line in lines:
        payee, amount, memo = line.strip().split(",")
        rows.append({"payee": payee, "amount": float(amount), "memo": memo})
    return rows
PY
cat > "$folder/README.md" <<'MD'
# ledger-lite

Imports bank exports into a ledger.
MD
echo "Demo project ready in $folder. Request to use:"
echo 'Bank exports with quoted payee names like "Smith, Jones & Co" break the importer: the row splits into too many columns. Use the csv module instead of split, and add a regression test.'
