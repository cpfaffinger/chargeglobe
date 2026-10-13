#!/bin/sh
# Runs all tests through the Compose test path (docs/adr/0010): builds the test images, runs
# test-dotnet and test-web one after the other, prints both coverage summaries and always
# removes the containers, networks and locally built images of the project "chargeglobe-test".
# Exit code 0 = all tests green and 100 % coverage in both suites.
set -u

cd "$(dirname "$0")/.."
compose="docker compose -f docker-compose.test.yml"

cleanup() {
    $compose down -v --rmi local --remove-orphans
}
trap cleanup EXIT

rm -rf artifacts
mkdir -p artifacts

if ! $compose build; then
    echo "RESULT: build of the test images failed"
    exit 1
fi

$compose run --rm -T test-dotnet
dotnet_status=$?

$compose run --rm -T test-web
web_status=$?

echo
echo "===== .NET (exit code $dotnet_status) ====="
grep -E "coverage:|Coverable lines:|Covered lines:" artifacts/dotnet/Summary.txt 2>/dev/null || echo "no summary"
echo "===== Angular (exit code $web_status) ====="
# Istanbul pads the labels; print them with single spaces ("Statements : 100% ( n/n )").
sed -E "s/ +/ /g" artifacts/web/summary.txt 2>/dev/null || echo "no summary"
echo

if [ "$dotnet_status" -ne 0 ] || [ "$web_status" -ne 0 ]; then
    echo "RESULT: FAILED"
    exit 1
fi

echo "RESULT: OK"
