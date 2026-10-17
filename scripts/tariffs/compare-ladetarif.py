#!/usr/bin/env python3
"""Compare the curated tariff catalog with the products on ladetarif.at (E-Control tariff calculator).

For manual use only (docs/endpunkte/ladetarif.md): ladetarif.at publishes no licence for its data, so
chargeglobe does not import it. This script reads the public API politely (one request every 2 s or
slower, about 45 requests per run), converts the net prices to gross and prints where the catalog
differs, which products the catalog lacks and where ladetarif.at has a newer date. Nothing is written
to the catalog; corrections are made by hand in src/ChargeGlobe.Providers.TariffCatalog/catalog/at/.

    python3 scripts/tariffs/compare-ladetarif.py                      # live, prints a report
    python3 scripts/tariffs/compare-ladetarif.py --cache /tmp/lt      # keeps the responses for a second run
    python3 scripts/tariffs/compare-ladetarif.py --cache /tmp/lt --offline --json   # from the cache, as JSON

Exit code 0 without differences, 1 with differences or missing products, 2 on errors.
"""
import argparse
import glob
import json
import os
import sys
import time
import urllib.request
from datetime import date

BASE = "https://www.ladetarif.at/api"
USER_AGENT = "chargeglobe-compare/1.0 (+https://chargeglobe.net)"
VAT = 1.2
TOLERANCE = 0.011

# The calculation needs the vehicle's power; areas filter products, so all states are asked for.
CALCULATE = {
    "vehicle": {"consumptionKwh": 19, "maxChargeKwAc": 11, "maxChargeKwDc": 150},
    "annualDrivingDistanceKm": 15000,
    "publicChargingPercentage": 100,
    "areas": ["BURGENLAND", "KAERNTEN", "NIEDEROESTERREICH", "OBEROESTERREICH", "SALZBURG", "STEIERMARK", "TIROL",
              "VORARLBERG", "WIEN", "OTHER"],
}

# (brand, product) on ladetarif.at -> (emp, tariff) in the catalog; None = deliberately not in the catalog.
PRODUCTS = {
    ("lidl österreich gmbh", "lidl plus app"): ("lidl", "lidl-plus"),
    ("tiwag - tiroler wasserkraft ag", "tiwag mobil"): ("tiwag", "tiwag-mobil"),
    ("wien energie", "tanke kwh go"): ("wien-energie", "tanke-kwh-go"),
    ("wien energie", "tanke kwh smart"): ("wien-energie", "tanke-kwh-smart"),
    ("wien energie", "tanke kwh pro"): ("wien-energie", "tanke-kwh-pro"),
    ("wien energie", "tanke zeit"): ("wien-energie", "tanke-zeit"),
    ("elinx", "ladetarif s"): None,  # brand discontinued in September 2026 (docs/07)
    ("elinx", "ladetarif m"): None,
    ("elinx", "ladetarif l"): None,
    ("strommobil", "strommobil easy power"): ("linz-ag", "easy-power"),
    ("strommobil", "strommobil more power"): ("linz-ag", "more-power"),
    ("strommobil", "strommobil partner"): ("linz-ag", "partner"),
    ("salzburg ag stromladen", "stromladen easy"): ("salzburg-ag", "stromladen-easy"),
    ("salzburg ag stromladen", "stromladen pro"): ("salzburg-ag", "stromladen-pro"),
    ("stadtwerke kufstein", "lade start"): ("stadtwerke-kufstein", "lade-start"),
    ("stadtwerke kufstein", "lade plus"): ("stadtwerke-kufstein", "lade-plus"),
    ("evn", "#drive easy"): ("evn", "drive-easy"),
    ("evn", "#drive smart"): ("evn", "drive-smart"),
    ("energie graz", "energie graz ladekarte"): ("energie-graz", "charge-public"),
    ("kelag", "kelag estart"): ("kelag", "estart"),
    ("kelag", "kelag eplus"): ("kelag", "eplus"),
    ("kelag", "kelag emax"): ("kelag", "emax"),
    ("energie steiermark", "ladetarif s"): ("energie-steiermark", "ladetarif-s"),
    ("energie steiermark", "ladetarif m"): ("energie-steiermark", "ladetarif-m"),
    ("energie steiermark", "ladetarif l"): ("energie-steiermark", "ladetarif-l"),
    ("öamtc epower", "öamtc epower (kwh-basiert)"): ("oeamtc", "epower"),
    ("smatrics", "charge simple"): ("smatrics", "charge-simple"),
    ("smatrics", "charge & roam"): ("smatrics", "charge-and-roam"),
    ("smatrics", "charge & roam+"): ("smatrics", "charge-and-roam-plus"),
    ("stw emobil", "stadtwerke klagenfurt ag - stw emobil"): ("energie-klagenfurt", "stw-emobil"),
    ("omv emotion premium", "omv emotion premium"): ("omv", "premium"),
    ("omv emotion premium", "omv emotion premium plus"): ("omv", "premium-plus"),
    ("salzachsonne", "salzachsonne ladetarife"): ("salzachsonne", "ladetarif"),
    ("illwerke vkw", "public"): ("vkw", "public-privat"),
    ("ikb", "ikb-e-laden"): ("ikb", "ikb-e-laden"),
    ("v-go", "v-go basic"): ("verbund", "v-go-basic"),
    ("v-go", "v-go premium"): ("verbund", "v-go-premium"),
    ("ella", "ella ladetarif"): None,  # ella's own tariff ended; ella belongs to the network of Wien Energie
    ("burgenland energie", "bessere energie laden"): ("burgenland-energie", "e-mobilitaetstarif"),
    ("burgenland energie", "bessere energie laden - 1-phasig"): ("burgenland-energie", "e-mobilitaetstarif-1-phasig"),
    ("energie ag", "energie mobil"): ("energie-ag", "energie-mobil"),
    ("energie ag", "energie mobil+"): ("energie-ag", "energie-mobil-plus"),
    ("wels strom", "mobil strom"): None,  # replaced by the e.Ladekarte (docs/07)
    ("da emobil", "da emobil lade- und tankkarte"): ("da-emobil", "ladekarte"),
}

# Providers that entered gross instead of net prices (docs/07); their amounts are taken as they are.
GROSS_ENTERED = {"salzachsonne", "ikb", "da-emobil"}

# Differences that are checked and where the catalog is right: (emp/tariff, field or None for all) -> reason.
EXPLAINED = {
    ("lidl/lidl-plus", "pricePerKwh"): "Lidl lowered DC to 0.29 EUR/kWh; ladetarif.at still has 2024",
    ("evn/drive-smart", None): "blocking fee from the end of charging is not modelled on ladetarif.at",
    ("evn/drive-easy", None): "blocking fee from the end of charging is not modelled on ladetarif.at",
    ("salzburg-ag/stromladen-easy", None): "ladetarif.at has the state of 2023-12, the catalog the product sheet of 2026-07",
    ("salzburg-ag/stromladen-pro", None): "ladetarif.at has the state of 2023-12, the catalog the product sheet of 2026-07",
    ("vkw/public-privat", "pricePerKwh"): "the catalog has the special discount until 2027-03-31 (a discount on ladetarif.at)",
    ("smatrics/charge-and-roam-plus", "monthlyFee"): "SMATRICS' own price sheet says 8.90 EUR",
    ("burgenland-energie/e-mobilitaetstarif", "blockingFeePerMinute"): "Burgenland Energie's own price sheet says 0.15 EUR/min at DC",
}

# Dates within this many days count as the same state (providers enter products a few days after the price sheet).
SAME_STATE_DAYS = 31


def key(text):
    return " ".join((text or "").lower().split())


class Api:
    def __init__(self, cache, offline, delay):
        self.cache, self.offline, self.delay, self.last = cache, offline, delay, 0.0
        if cache:
            os.makedirs(cache, exist_ok=True)

    def get(self, name, path, body=None):
        file = os.path.join(self.cache, name + ".json") if self.cache else None
        if file and os.path.exists(file):
            with open(file, encoding="utf-8") as handle:
                return json.load(handle)
        if self.offline:
            raise SystemExit(f"{name} is not in the cache {self.cache}")
        wait = self.last + self.delay - time.monotonic()
        if wait > 0:
            time.sleep(wait)
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(BASE + path, data=data, headers={"User-Agent": USER_AGENT, "Accept": "application/json",
                                                                         **({"Content-Type": "application/json"} if data else {})})
        with urllib.request.urlopen(request, timeout=60) as response:
            result = json.load(response)
        self.last = time.monotonic()
        if file:
            with open(file, "w", encoding="utf-8") as handle:
                json.dump(result, handle, ensure_ascii=False)
        return result


def gross(value, factor):
    return None if value is None else round(value * factor, 3)


def at(fees, current, power):
    """The entry of a ladetarif power class list that covers the power (`to` is the inclusive upper limit)."""
    covering = sorted((fee for fee in fees if fee["current"] == current), key=lambda fee: fee.get("to") or 10_000)
    return next((fee for fee in covering if (fee.get("to") or 10_000) >= power), None)


def theirs_at(tariff, current, power, factor):
    fee, block = at(tariff.get("variableFees", []), current, power), at(tariff.get("blockingFees", []), current, power)
    if fee is None:
        return None
    return {
        "pricePerKwh": gross(fee.get("priceDayConsumptionEuro") or None, factor),
        "pricePerMinute": gross(fee.get("priceDayTimeEuro") or None, factor),
        "blockingFeePerMinute": gross(block["priceDayTimeEuro"], factor) if block else None,
        "blockingAfterMinutes": block["timeLimit"] - 1 if block else None,
    }


def ours_at(zone, current, power):
    """The catalog's price of a zone at the power, without time windows or weekdays (highest minKw not above it)."""
    prices = [price for price in zone.get("prices", []) if price["current"] == current and not price.get("startTime") and not price.get("days")
              and (price.get("minKw") or 0) <= power]
    if not prices:
        return None
    price = max(prices, key=lambda item: item.get("minKw") or 0)
    blocking = price.get("blockingFeePerMinute")
    return {
        "pricePerKwh": price.get("pricePerKwh"),
        "pricePerMinute": price.get("pricePerMinute"),
        "blockingFeePerMinute": blocking,
        "blockingAfterMinutes": price.get("blockingAfterMinutes") if blocking is not None else None,
    }


def monthly(product, factor):
    total = 0.0
    for fee in product.get("fixedFees", []):
        if fee.get("typeId") == 1:
            divisor = {"MONTH": 1, "QUARTER": 3, "YEAR": 12, "TWELVE_MONTHS": 12}.get(fee["interval"])
            if divisor:
                total += fee["priceEuro"] * factor / divisor
    return round(total, 2)


def load_catalog(directory):
    tariffs = {}
    for path in glob.glob(os.path.join(directory, "*.json")):
        with open(path, encoding="utf-8") as handle:
            for emp in json.load(handle).get("emps", []):
                for tariff in emp["tariffs"]:
                    tariffs[(emp["key"], tariff["key"])] = tariff
    return tariffs


# Power points at which catalog and ladetarif.at are compared (kW per current).
POINTS = {"AC": (3.7, 11, 22), "DC": (50, 100, 150, 300)}


def compare_tariff(main, zone, factor):
    found = []
    for current, powers in POINTS.items():
        for power in powers:
            theirs, ours = theirs_at(main, current, power, factor), ours_at(zone, current, power)
            if theirs is None or ours is None:
                if (theirs is None) != (ours is None) and power == powers[0]:
                    found.append({"field": "current", "current": current, "power": power, "catalog": ours is not None, "ladetarif": theirs is not None})
                continue
            for field, value in ours.items():
                other = theirs[field]
                if value is None and other is None:
                    continue
                if field == "blockingAfterMinutes" and value is not None and other is not None and abs(value - other) <= 1:
                    continue  # "ab der 241. Minute" and "ab der 240. Minute" are entered both ways
                if value is None or other is None or abs(float(value) - float(other)) > TOLERANCE:
                    found.append({"field": field, "current": current, "power": power, "catalog": value, "ladetarif": other})
    # One line per field and value pair, with the powers it applies to.
    merged = {}
    for row in found:
        merged.setdefault((row["field"], row["current"], str(row["catalog"]), str(row["ladetarif"])), {**row, "power": []})["power"].append(row["power"])
    return list(merged.values())


def compare(product, tariff, emp_key):
    """Differences of the home zone and the monthly fee; of several main tariffs the one closest to the catalog counts."""
    factor = 1.0 if emp_key in GROSS_ENTERED else VAT
    found = []
    fee = monthly(product, factor)
    if abs((tariff.get("monthlyFee") or 0) - fee) > TOLERANCE:
        found.append({"field": "monthlyFee", "catalog": tariff.get("monthlyFee") or 0, "ladetarif": fee})
    home = next((zone for zone in tariff["zones"] if zone["kind"] in ("HOME", "OPERATOR")), None)
    mains = (product.get("tariffs") or {}).get("main") or []
    if home is None or not mains:
        return found + [{"field": "zone", "catalog": home is not None, "ladetarif": bool(mains)}]
    return found + min((compare_tariff(main, home, factor) for main in mains), key=len)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--catalog", default=os.path.join(os.path.dirname(__file__), "..", "..", "src", "ChargeGlobe.Providers.TariffCatalog", "catalog", "at"))
    parser.add_argument("--cache", help="directory for the raw responses (not in the repository)")
    parser.add_argument("--offline", action="store_true", help="read only from --cache")
    parser.add_argument("--delay", type=float, default=2.0, help="seconds between requests, at least 2")
    parser.add_argument("--json", action="store_true", help="print the report as JSON")
    args = parser.parse_args()
    if args.delay < 2:
        parser.error("--delay must be at least 2 seconds")

    api = Api(args.cache, args.offline, args.delay)
    catalog = load_catalog(args.catalog)
    products = api.get("calculate", "/calculate?page=0&size=200", CALCULATE)["products"]
    report = {"differences": [], "explained": [], "missing": [], "newer": [], "ignored": [], "unmatched": []}
    for item in products:
        name = (key(item["brandName"]), key(item["name"]))
        if name not in PRODUCTS:
            report["unmatched"].append({"id": item["id"], "provider": item["providerName"].strip(), "brand": item["brandName"].strip(), "product": item["name"].strip()})
            continue
        target = PRODUCTS[name]
        if target is None:
            report["ignored"].append(f"{item['brandName'].strip()} {item['name'].strip()}")
            continue
        tariff = catalog.get(target)
        if tariff is None:
            report["missing"].append({"id": item["id"], "brand": item["brandName"].strip(), "product": item["name"].strip(), "expected": "/".join(target)})
            continue
        product = api.get(f"product-{item['id']}", f"/products/{item['id']}")
        valid_from = (product.get("validFrom") or "")[:10]
        ours = tariff.get("sourceDate")
        if valid_from and (not ours or (date.fromisoformat(valid_from) - date.fromisoformat(ours)).days > SAME_STATE_DAYS):
            report["newer"].append({"tariff": "/".join(target), "catalog": ours, "ladetarif": valid_from})
        name = "/".join(target)
        for difference in compare(product, tariff, target[0]):
            reason = EXPLAINED.get((name, difference["field"])) or EXPLAINED.get((name, None))
            row = {"tariff": name, "ladetarifId": item["id"], "validFrom": valid_from, **difference}
            (report["explained"] if reason else report["differences"]).append({**row, **({"reason": reason} if reason else {})})

    if args.json:
        json.dump(report, sys.stdout, ensure_ascii=False, indent=2)
        print()
    else:
        print(f"ladetarif.at: {len(products)} products; {len(report['differences'])} differences ({len(report['explained'])} more explained), "
              f"{len(report['missing'])} missing, "
              f"{len(report['newer'])} newer, {len(report['unmatched'])} without mapping, {len(report['ignored'])} ignored")
        for row in report["differences"]:
            where = f"{row['current']} {'/'.join(str(power) for power in row['power'])} kW" if row.get("current") else ""
            print(f"  {row['tariff']:<46} {row['field']:<21} {where:<22} catalog {row['catalog']!s:<7} ladetarif {row['ladetarif']!s:<7} (valid from {row['validFrom']})")
        for name in sorted({(row["tariff"], row["reason"]) for row in report["explained"]}):
            print(f"  explained: {name[0]}: {name[1]}")
        for row in report["missing"]:
            print(f"  missing in catalog: {row['brand']} {row['product']} (id {row['id']}, expected {row['expected']})")
        for row in report["newer"]:
            print(f"  newer on ladetarif.at: {row['tariff']} catalog {row['catalog']} < {row['ladetarif']}")
        for row in report["unmatched"]:
            print(f"  without mapping: {row['brand']} | {row['product']} (id {row['id']}) - add it to PRODUCTS")
        for row in report["ignored"]:
            print(f"  ignored: {row}")
    return 1 if report["differences"] or report["missing"] or report["unmatched"] else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError) as error:
        print(f"error: {error}", file=sys.stderr)
        sys.exit(2)
