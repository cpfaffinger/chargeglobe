#!/bin/sh
# Entrypoint of the test-web container: runs the Angular tests with coverage and writes the
# summary to /artifacts/web. Exit code != 0 if a test fails or statement coverage is below 100 %.
set -u

out=/artifacts/web
rm -rf "$out" coverage
mkdir -p "$out"

# Chromium runs as root inside the container, which requires the launcher without sandbox.
npx ng test --code-coverage --watch=false --browsers=ChromeHeadlessNoSandbox
test_status=$?

cp coverage/web/summary.txt "$out/summary.txt" 2>/dev/null

if [ "$test_status" -ne 0 ]; then
    echo "FAILED: ng test exited with $test_status"
    exit "$test_status"
fi

if ! grep -Eq "Statements +: 100%" "$out/summary.txt"; then
    echo "FAILED: statement coverage is below 100 %"
    exit 1
fi
