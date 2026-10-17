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
  /** The zone applies only to EVSEs of these currents; null for both. */
  currents?: ('AC' | 'DC')[] | null;
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
}
