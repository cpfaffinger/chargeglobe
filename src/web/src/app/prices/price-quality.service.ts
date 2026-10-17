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

/** "0,005 €/kWh · Start 0,00 €", "nichts berechnet" for a sample without any amount. */
export function sampleText(sample: PriceSample): string {
  const euro = (amount: number) => new Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR', maximumFractionDigits: 4 }).format(amount);
  const parts = [
    sample.pricePerKwh ? `${euro(sample.pricePerKwh)}/kWh` : null,
    sample.pricePerMinute ? `${euro(sample.pricePerMinute)}/min` : null,
    sample.sessionFee ? `Start ${euro(sample.sessionFee)}` : null,
  ].filter((part) => part !== null);
  return `${parts.length > 0 ? parts.join(' · ') : 'nichts berechnet'} – ${REASONS[sample.reason]}`;
}
