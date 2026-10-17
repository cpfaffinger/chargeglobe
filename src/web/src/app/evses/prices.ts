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
  /** False if the register's price is implausible (outside price.min/max_plausible_kwh, or 0 without free of charge). */
  plausible?: boolean;
  /** Why it is implausible: no amount reported, energy price below or above the bounds; null if plausible. */
  reason?: PriceReason | null;
}

export type PriceReason = 'NO_AMOUNT' | 'BELOW_MIN' | 'ABOVE_MAX';

/** Shown instead of the cost of an implausible ad-hoc price: "kein Preis gemeldet" or "Preis laut Register vermutlich falsch". */
export const implausibleText = (price: Pick<PriceView, 'reason'>): string =>
  price.reason === 'NO_AMOUNT' ? 'kein Preis gemeldet' : 'Preis laut Register vermutlich falsch';

/** Whether the price may be shown with its cost (missing flag: plausible). */
export const plausible = (price: PriceView): boolean => price.plausible !== false;

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
  /** Since when the prices of the tariff are as they are; null if they never changed. */
  priceChangedAt?: string | null;
}

/** How long a price change is pointed out at a charge point. */
export const RECENT_CHANGE_DAYS = 30;

/** The day of a price change of the last RECENT_CHANGE_DAYS days, else null. */
export function recentChange(card: Pick<EmpPrice, 'priceChangedAt'>, now = Date.now()): string | null {
  const changed = card.priceChangedAt ? Date.parse(card.priceChangedAt) : NaN;
  return now - changed <= RECENT_CHANGE_DAYS * 24 * 60 * 60 * 1000 ? card.priceChangedAt! : null;
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
    /** Hint of the operator's network about the prices here (e.g. Tesla), with the operator's page. */
    priceNote?: { text: string; url: string | null } | null;
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

/**
 * Whether the card is one of the user's: its tariff ("emp/tariff") or, for a provider chosen without tariff, its provider
 * is among the own keys (CardsStore.ownKeys).
 */
export const isOwnCard = (card: Pick<EmpPrice, 'emp' | 'tariffKey'>, own: readonly string[]): boolean =>
  own.includes(`${card.emp}/${card.tariffKey}`) || own.includes(card.emp);

/** The cards of the list: all, or with onlyOwn only the user's. */
export const shownCards = (emps: EmpPrice[], own: readonly string[], onlyOwn: boolean): EmpPrice[] =>
  onlyOwn ? emps.filter((card) => isOwnCard(card, own)) : emps;

/** Ad-hoc price and cards (with onlyOwn only the user's) by the cost of the example; empty without cards (nothing to compare). */
export function comparison(data: EvsePricesResponse['data'], cards: readonly string[] = [], onlyOwn = false): PriceRow[] {
  if (data.emps.length === 0) {
    return [];
  }
  const rows = [
    ...(data.current && plausible(data.current.price) ? [{ key: 'ad-hoc', label: 'Ad-hoc (ohne Vertrag)', price: data.current.price, monthlyFee: null, own: false }] : []),
    ...shownCards(data.emps, cards, onlyOwn).map((emp) => ({
      key: emp.tariffId,
      label: `${emp.empName} · ${emp.tariff} (${zoneText(emp)})`,
      price: emp.price,
      monthlyFee: emp.monthlyFee,
      own: isOwnCard(emp, cards),
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
