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
}

export interface EvsePricesResponse {
  data: {
    id: string;
    evseId: string;
    tariffKind: string;
    from: string;
    to: string;
    example: { kwh: number; minutes: number };
    current: { price: PriceView; since: string; observedAt: string } | null;
    history: { price: PriceView; from: string; to: string | null }[];
  };
  meta: { attribution: { source: string; text: string; license: string }[] };
}

/** An amount in the currency, German notation with a non-breaking space (e.g. "0,59 €"). */
export const money = (amount: number, currency: string): string =>
  new Intl.NumberFormat('de-DE', { style: 'currency', currency, minimumFractionDigits: 2, maximumFractionDigits: 4 }).format(amount);

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
    const from = price.blockingAfterMinutes === null ? '' : ` ab Minute ${price.blockingAfterMinutes}`;
    parts.push(`Blockiergebühr ${money(price.blockingFeePerMinute, price.currency)}/min${from}`);
  }
  return parts.length > 0 ? parts : ['keine Angaben'];
}
