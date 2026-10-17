import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Element of GET /api/v1/emps: a charging card or app. */
export interface Emp {
  key: string;
  name: string;
  website: string | null;
  country: string;
  /** Number of current tariffs. */
  tariffs: number;
}

/** Element of GET /api/v1/networks: operators by the operator part of their EVSE-IDs. */
export interface Network {
  key: string;
  name: string;
  country: string;
  operators: string[];
  /** Hint about the prices of the network's charge points and the operator's page; null for none. */
  priceNote?: string | null;
  priceUrl?: string | null;
  /** Number of sites with their own price page. */
  sites?: number;
}

export type ZoneKind = 'OPERATOR' | 'HOME' | 'PARTNER' | 'ROAMING';

/** A price element of a zone; amounts null if the tariff has no such part. */
export interface PriceElement {
  current: 'AC' | 'DC';
  /** From this maximum power of the EVSE on (HPC stages); null for any. */
  minKw: number | null;
  pricePerKwh: number | null;
  pricePerMinute: number | null;
  sessionFee: number | null;
  blockingFeePerMinute: number | null;
  blockingAfterMinutes: number | null;
  /** Local time window "HH:mm" of the session start (e.g. a night price); null for any time. */
  startTime?: string | null;
  endTime?: string | null;
  /** Highest blocking fee of a session. */
  maxBlockingFee?: number | null;
  /** CHARGING_END: blockingAfterMinutes count from the end of charging. */
  blockingFrom?: 'SESSION_START' | 'CHARGING_END';
  /** ISO weekdays 1–7 of the session start; null for every day. */
  days?: number[] | null;
  /** Blocked minutes are billed only in this local window. */
  blockingStartTime?: string | null;
  blockingEndTime?: string | null;
  maxBlockingMinutes?: number | null;
}

export interface Zone {
  kind: ZoneKind;
  /** FIXED: the prices of the zone; OPERATOR_AD_HOC: the ad-hoc price of the EVSE with the markups; UNKNOWN: not published. */
  pricing: 'FIXED' | 'OPERATOR_AD_HOC' | 'UNKNOWN';
  /** Percent on the operator's price (−10 = 10 % off). */
  markupPercent: number | null;
  /** Fee of the provider per session on top of the operator's price. */
  markupSessionFee: number | null;
  /** With OPERATOR_AD_HOC: EUR per kWh on the operator's energy price, e.g. −0.25. */
  markupPerKwh?: number | null;
  /** The zone applies only to EVSEs of these currents; null for both. */
  currents?: ('AC' | 'DC')[] | null;
  /** The zone applies only to EVSEs in these countries (ISO 3166-1 alpha-2, prices abroad); null for every country. */
  countries?: string[] | null;
  networks: string[];
  operators: string[];
  excludedNetworks: string[];
  excludedOperators: string[];
  prices: PriceElement[];
  notes: string | null;
}

/** Element of GET /api/v1/tariffs: one version of a tariff of a provider. */
export interface Tariff {
  id: string;
  emp: string;
  empName: string;
  key: string;
  name: string;
  currency: string;
  monthlyFee: number | null;
  prerequisites: string | null;
  markets: string[];
  zones: Zone[];
  notes: string | null;
  sourceUrl: string;
  /** "Stand" of the provider (ISO date). */
  sourceDate: string | null;
  /** When the values were last checked against the source (ISO date). */
  checkedOn?: string | null;
  confidence: 'HIGH' | 'MEDIUM' | 'LOW';
  validFrom: string;
  validTo: string | null;
  /** The "Stand" is unknown or older than the setting tariff.stale_months: the values need a check. */
  stale: boolean;
}

/** One price change from the version before (GET /tariffs/{emp}/{key}/history). */
export interface TariffChange {
  kind: string;
  zone: string | null;
  current: 'AC' | 'DC' | null;
  minKw: number | null;
  from: number | null;
  to: number | null;
}

export interface TariffVersion {
  id: string;
  validFrom: string;
  validTo: string | null;
  sourceDate: string | null;
  checkedOn: string | null;
  monthlyFee: number | null;
  changes: TariffChange[];
}

export interface TariffHistory {
  emp: string;
  empName: string;
  key: string;
  name: string;
  versions: TariffVersion[];
}

const CHANGE_LABELS: Record<string, [string, string]> = {
  MONTHLY_FEE: ['Grundgebühr', '€/Monat'],
  PRICE_PER_KWH: ['Energie', '€/kWh'],
  PRICE_PER_MINUTE: ['Zeit', '€/min'],
  SESSION_FEE: ['Startgebühr', '€'],
  BLOCKING_FEE_PER_MINUTE: ['Blockiergebühr', '€/min'],
  BLOCKING_AFTER_MINUTES: ['Blockieren ab Minute', ''],
  MAX_BLOCKING_FEE: ['Deckel Blockiergebühr', '€'],
  MARKUP_PERCENT: ['Aufschlag', '%'],
  MARKUP_SESSION_FEE: ['Aufschlag je Ladevorgang', '€'],
  MARKUP_PER_KWH: ['Aufschlag je kWh', '€/kWh'],
};

/** "HOME evn · AC ab 150 kW: Energie 0,45 → 0,49 €/kWh", "Zone ROAMING (DE) neu", "PARTNER beoe entfällt". */
export function changeText(change: TariffChange): string {
  const value = (amount: number | null) => (amount === null ? '–' : new Intl.NumberFormat('de-DE', { maximumFractionDigits: 4 }).format(amount));
  const where = [change.zone, change.current ? `${change.current}${change.minKw ? ` ab ${change.minKw} kW` : ''}` : null].filter((part) => part).join(' · ');
  switch (change.kind) {
    case 'ZONE_ADDED':
      return `Zone ${change.zone} neu`;
    case 'ZONE_REMOVED':
      return `Zone ${change.zone} entfällt`;
    case 'PRICE_ADDED':
      return `${where}: neuer Preis ${value(change.to)}`;
    case 'PRICE_REMOVED':
      return `${where}: Preis ${value(change.from)} entfällt`;
    default: {
      const [label, unit] = CHANGE_LABELS[change.kind] ?? [change.kind, ''];
      return `${where ? `${where}: ` : ''}${label} ${value(change.from)} → ${value(change.to)}${unit ? ` ${unit}` : ''}`;
    }
  }
}

/** German names of the zones, as the providers call them. */
export const ZONE_LABELS: Record<ZoneKind, string> = {
  OPERATOR: 'Sonderpreis',
  HOME: 'Heimnetz',
  PARTNER: 'Partnernetz',
  ROAMING: 'Roaming',
};

/** Client of the providers, networks and tariffs (price:read). */
@Injectable({ providedIn: 'root' })
export class TariffsService {
  private readonly http = inject(HttpClient);

  async emps(): Promise<Emp[]> {
    return (await firstValueFrom(this.http.get<{ data: Emp[] }>('/api/v1/emps'))).data;
  }

  async networks(): Promise<Network[]> {
    return (await firstValueFrom(this.http.get<{ data: Network[] }>('/api/v1/networks'))).data;
  }

  async tariffs(): Promise<Tariff[]> {
    return (await firstValueFrom(this.http.get<{ data: Tariff[] }>('/api/v1/tariffs'))).data;
  }

  async history(emp: string, key: string): Promise<TariffHistory> {
    return (await firstValueFrom(this.http.get<{ data: TariffHistory }>(`/api/v1/tariffs/${encodeURIComponent(emp)}/${encodeURIComponent(key)}/history`))).data;
  }
}
