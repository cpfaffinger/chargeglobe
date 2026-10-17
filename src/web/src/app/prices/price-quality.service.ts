import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PriceReason } from '../evses/prices';

/** One price that occurs at implausible EVSEs of an operator. */
export interface PriceSample {
  pricePerKwh: number | null;
  pricePerMinute: number | null;
  sessionFee: number | null;
  evses: number;
  reason: PriceReason;
  /** The energy price probably meant (×100 or ÷100 within the bounds); null if none fits. */
  intendedPricePerKwh?: number | null;
}

/** The implausible ad-hoc prices of one operator. */
export interface OperatorImplausiblePrices {
  operatorId: string | null;
  name: string | null;
  evses: number;
  examples: PriceSample[];
  lastObservedAt: string;
}

/** GET /api/v1/admin/prices/implausible (jobs:read). */
export interface ImplausiblePrices {
  at: string;
  minKwh: number;
  maxKwh: number;
  evses: number;
  noAmount: number;
  belowMin: number;
  aboveMax: number;
  operators: OperatorImplausiblePrices[];
}

/** German labels of the reasons. */
export const REASONS: Record<PriceReason, string> = {
  NO_AMOUNT: 'kein Preis gemeldet',
  BELOW_MIN: 'unter der Spanne',
  ABOVE_MAX: 'über der Spanne',
};

/** Client of the checks of the ad-hoc prices of the register. */
@Injectable({ providedIn: 'root' })
export class PriceQualityService {
  private readonly http = inject(HttpClient);

  async implausible(): Promise<ImplausiblePrices> {
    return (await firstValueFrom(this.http.get<{ data: ImplausiblePrices }>('/api/v1/admin/prices/implausible'))).data;
  }

  /** The report as CSV, one row per EVSE (export:read). */
  async export(): Promise<Blob> {
    return firstValueFrom(this.http.get('/api/v1/admin/prices/implausible/export', { responseType: 'blob' }));
  }
}

const euro = (amount: number) => new Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR', maximumFractionDigits: 4 }).format(amount);

/** "0,005 €/kWh · Start 0,00 € – unter der Spanne, vermutlich 0,50 €/kWh"; "nichts berechnet" for a sample without any amount. */
export function sampleText(sample: PriceSample): string {
  const parts = [
    sample.pricePerKwh ? `${euro(sample.pricePerKwh)}/kWh` : null,
    sample.pricePerMinute ? `${euro(sample.pricePerMinute)}/min` : null,
    sample.sessionFee ? `Start ${euro(sample.sessionFee)}` : null,
  ].filter((part) => part !== null);
  const intended = sample.intendedPricePerKwh ? `, vermutlich ${euro(sample.intendedPricePerKwh)}/kWh` : '';
  return `${parts.length > 0 ? parts.join(' · ') : 'nichts berechnet'} – ${REASONS[sample.reason]}${intended}`;
}

/**
 * A draft of a message to the operator or the E-Control about the implausible prices of one operator, to copy into the
 * user's own mail program (the app sends nothing).
 */
export function mailDraft(operator: OperatorImplausiblePrices, at: string): string {
  const name = operator.name ?? 'Betreiber';
  const id = operator.operatorId ? ` (${operator.operatorId})` : '';
  const date = new Intl.DateTimeFormat('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric' }).format(new Date(at));
  const points = operator.evses === 1 ? '1 Ladepunkt' : `${operator.evses} Ladepunkte`;
  return [
    `Betreff: Ad-hoc-Preise im Ladestellenverzeichnis – ${name}${id}`,
    '',
    'Guten Tag,',
    '',
    `im Ladestellenverzeichnis der E-Control (ladestellen.at) sind für ${points} von ${name}${id} Ad-hoc-Preise eingetragen, die vermutlich nicht stimmen (Stand ${date}):`,
    ...operator.examples.map((sample) => `- ${sampleText(sample)} (${sample.evses === 1 ? '1 Ladepunkt' : `${sample.evses} Ladepunkte`})`),
    '',
    'Die einzelnen Ladepunkte (EVSE-IDs) stehen in der beiliegenden Liste. Bitte prüfen Sie die Angaben und korrigieren Sie sie im',
    'Ladestellenverzeichnis; Fragen zum Verzeichnis beantwortet die E-Control über die Kontaktmöglichkeiten auf ladestellen.at.',
    '',
    'Mit freundlichen Grüßen',
  ].join('\n');
}
