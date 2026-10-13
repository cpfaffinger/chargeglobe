#!/bin/sh
# Entrypoint of the test-dotnet container: runs all .NET tests with coverage and writes the
# summary to /artifacts/dotnet. Exit code != 0 if a test fails or line coverage is below 100 %.
set -u

out=/artifacts/dotnet
rm -rf "$out" tests/*/TestResults
mkdir -p "$out"

dotnet test --no-build --collect:"XPlat Code Coverage"
test_status=$?

dotnet tool run reportgenerator \
    "-reports:tests/*/TestResults/*/coverage.cobertura.xml" \
    "-targetdir:$out" \
    "-reporttypes:TextSummary;Cobertura" > /dev/null || exit 1

# Lines without a hit, as "file:line", to make a coverage gap actionable.
awk '
    /<class / { match($0, /filename="[^"]*"/); file = substr($0, RSTART + 10, RLENGTH - 11) }
    /<line / && /hits="0"/ { match($0, /number="[0-9]+"/); print file ":" substr($0, RSTART + 8, RLENGTH - 9) }
' "$out/Cobertura.xml" | sort -u > "$out/uncovered.txt"

cat "$out/Summary.txt"

if [ "$test_status" -ne 0 ]; then
    echo "FAILED: dotnet test exited with $test_status"
    exit "$test_status"
fi

if ! grep -q "Line coverage: 100%" "$out/Summary.txt"; then
    echo "FAILED: line coverage is below 100 %. Uncovered lines:"
    cat "$out/uncovered.txt"
    exit 1
fi
