#!/usr/bin/env bash
# Syntax-checks every dashboard module.
#
# The dashboard has no build step, which is the point: it stays readable and hackable by whoever
# is running the server. The trade is that nothing catches a stray bracket before the browser
# does, so this runs as part of CI instead.
set -uo pipefail

cd "$(dirname "$0")/../web/dashboard" || exit 1

failed=0
for file in js/*.js js/views/*.js; do
    if ! output=$(node --input-type=module --check < "$file" 2>&1); then
        echo "FAIL $file"
        echo "$output" | head -6
        failed=1
    else
        echo "  ok $file"
    fi
done

if [ "$failed" -ne 0 ]; then
    echo
    echo "Dashboard JavaScript has syntax errors."
    exit 1
fi

echo
echo "All dashboard modules parse cleanly."
