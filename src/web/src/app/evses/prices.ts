import { ZONE_LABELS, ZoneKind } from '../tariffs/tariffs.service';

/** Price components of GET /api/v1/evses/{evseId}/prices; amounts null if the source does not tell. */
export interface PriceView {
  source: string;
  currency: string;
  pricePerKwh: number | null;
  pricePerMinute: number | null;
  sessionFee: number | null;
  blockingFeePerMinute: number | null;
  blockingAfterMinutes: number | null;
  freeOfCharge: boolean;
  /** Cost of the example session (data.example). */
  exampleCost: number;
  /** Highest blocking fee of a session. */
  blockingFeeMax?: number | null;
  /** CHARGING_END: blockingAfterMinutes count from the end of charging. */
  blockingFrom?: 'SESSION_START' | 'CHARGING_END';
  /** Blocked minutes are billed only in this local window "HH:mm". */
  blockingStartTime?: string | null;
  blockingEndTime?: string | null;
  /** Highest number of billed blocking minutes. */
  blockingMaxMinutes?: number | null;
}

/** Price of a charging card or app at the EVSE (data.emps of GET /api/v1/evses/{evseId}/prices). */
export interface EmpPrice {
  emp: string;
  empName: string;
  tariffId: string;
  tariffKey: string;
  tariff: string;
  /** Where the EVSE lies for the tariff: special price of its operator, home or partner network, roaming. */
  zone: ZoneKind;
  /** OPERATOR_AD_HOC: the operator's ad-hoc price with the provider's markup. */
  pricing: 'FIXED' | 'OPERATOR_AD_HOC';
  /** Base fee per month of the card, not part of exampleCost. */
  monthlyFee: number | null;
  price: PriceView;
  sourceUrl: string;
  /** "Stand" of the provider (ISO date). */
  sourceDate: string | null;
  confidence: 'HIGH' | 'MEDIUM' | 'LOW';
}

/** A tariff valid now that cannot be used at the EVSE. */
export interface EmpUnavailable {
  emp: string;
  empName: string;
  tariffKey: string;
  tariff: string;
}

/** The session of exampleCost at the EVSE: the energy as long as it takes at the lower power of EVSE and example car. */
export interface SessionExample {
  kwh: number;
  minutes: number;
  powerKw: number | null;
  basis: 'EVSE' | 'VEHICLE' | 'REQUEST';
}

/** "Roaming" or, at the operator's price, "Roaming, Betreiberpreis". */
export function zoneText(card: Pick<EmpPrice, 'zone' | 'pricing'>): string {
  return card.pricing === 'OPERATOR_AD_HOC' ? `${ZONE_LABELS[card.zone]}, Betreiberpreis` : ZONE_LABELS[card.zone];
}

/** "16 min", "2 h", "2 h 44 min". */
export function duration(minutes: number): string {
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return hours === 0 ? `${rest} min` : rest === 0 ? `${hours} h` : `${hours} h ${rest} min`;
}

/** "30 kWh in 2 h 44 min". */
export function sessionText(example: SessionExample): string {
  return `${new Intl.NumberFormat('de-DE').format(example.kwh)} kWh in ${duration(example.minutes)}`;
}

export interface EvsePricesResponse {
  data: {
    id: string;
    evseId: string;
    tariffKind: string;
    from: string;
    to: string;
    example: SessionExample;
    current: { price: PriceView; since: string; observedAt: string } | null;
    history: { price: PriceView; from: string; to: string | null }[];
    /** The cards and apps that apply at the EVSE, cheapest example first. */
    emps: EmpPrice[];
    /** Tariffs that cannot be used at the EVSE. */
    unavailable: EmpUnavailable[];
    /** Tariffs usable at the EVSE at an unknown price: the operator's price without a known ad-hoc price, or unpublished. */
    priceUnknown: (EmpUnavailable & { zone: ZoneKind })[];
  };
  meta: { attribution: { source: string; text: string; license: string }[] };
}

/** An amount in the currency, German notation with a non-breaking space (e.g. "0,59 €"). */
export const money = (amount: number, currency: string): string =>
  new Intl.NumberFormat('de-DE', { style: 'currency', currency, minimumFractionDigits: 2, maximumFractionDigits: 4 }).format(amount);

/** A row of the price comparison: the ad-hoc price or a card. */
export interface PriceRow {
  key: string;
  label: string;
  price: PriceView;
  monthlyFee: number | null;
  /** Lowest cost of the example among the rows. */
  cheapest: boolean;
  /** A card the user has (own cards in the account). */
  own: boolean;
}

/** Ad-hoc price and cards by the cost of the example; empty without cards (nothing to compare). */
export function comparison(data: EvsePricesResponse['data'], cards: readonly string[] = []): PriceRow[] {
  if (data.emps.length === 0) {
    return [];
  }
  const rows = [
    ...(data.current ? [{ key: 'ad-hoc', label: 'Ad-hoc (ohne Vertrag)', price: data.current.price, monthlyFee: null, own: false }] : []),
    ...data.emps.map((emp) => ({
      key: emp.tariffId,
      label: `${emp.empName} · ${emp.tariff} (${zoneText(emp)})`,
      price: emp.price,
      monthlyFee: emp.monthlyFee,
      own: cards.includes(emp.emp),
    })),
  ].sort((left, right) => left.price.exampleCost - right.price.exampleCost);
  return rows.map((row) => ({ ...row, cheapest: row.price.exampleCost === rows[0].price.exampleCost }));
}

/** The parts of a price as short German phrases, e.g. ["0,59 €/kWh", "Startgebühr 0,50 €"]; "kostenlos" if free. */
export function priceParts(price: PriceView): string[] {
  if (price.freeOfCharge) {
    return ['kostenlos'];
  }
  const parts: string[] = [];
  if (price.pricePerKwh !== null) {
    parts.push(`${money(price.pricePerKwh, price.currency)}/kWh`);
  }
  if (price.pricePerMinute) {
    parts.push(`${money(price.pricePerMinute, price.currency)}/min`);
  }
  if (price.sessionFee) {
    parts.push(`Startgebühr ${money(price.sessionFee, price.currency)}`);
  }
  if (price.blockingFeePerMinute) {
    const end = price.blockingFrom === 'CHARGING_END' ? ' nach Ladeende' : '';
    const from = price.blockingAfterMinutes === null ? '' : ` ab Minute ${price.blockingAfterMinutes}${end}`;
    const window = price.blockingStartTime ? ` (${price.blockingStartTime}–${price.blockingEndTime} Uhr)` : '';
    const minutes = price.blockingMaxMinutes ? `, max. ${price.blockingMaxMinutes} min` : '';
    const cap = price.blockingFeeMax ? `, max. ${money(price.blockingFeeMax, price.currency)}` : '';
    parts.push(`Blockiergebühr ${money(price.blockingFeePerMinute, price.currency)}/min${from}${window}${minutes}${cap}`);
  }
  return parts.length > 0 ? parts : ['keine Angaben'];
}
