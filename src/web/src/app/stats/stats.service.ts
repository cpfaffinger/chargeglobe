import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface RegionCount {
  code: string;
  name: string;
  locations: number;
  evses: number;
}

/** Response of GET /api/v1/stats. */
export interface StatsResponse {
  data: {
    region: string | null;
    /** parks: charging parks; stations: charging stations as far as a source names them. */
    totals: { locations: number; evses: number; connectors: number; parks: number; stations: number };
    byRegion: RegionCount[];
    byPowerClass: { class: string; evses: number }[];
    byConnector: { standard: string; connectors: number }[];
    topOperators: { id: string; name: string; evses: number }[];
    /** Sites (a charging park, else a location alone) by their number of EVSEs. */
    evsesPerSite: { size: string; count: number }[];
    /** EVSEs by their number of connectors. */
    connectorsPerEvse: { size: string; count: number }[];
    status: { available: number; occupied: number; faulted: number; unknown: number; stale: number };
    freshness: { p50Minutes: number | null; p90Minutes: number | null };
    generatedAt: string;
  };
  meta: { attribution: { source: string; text: string; license: string }[] };
}

/** Labels of the size classes of GET /api/v1/stats. */
export const SITE_SIZES: Record<string, string> = {
  '1': '1 Ladepunkt',
  '2': '2 Ladepunkte',
  '3_4': '3–4 Ladepunkte',
  '5_10': '5–10 Ladepunkte',
  '11_PLUS': 'mehr als 10',
};

export const CONNECTOR_SIZES: Record<string, string> = {
  '0': 'ohne Angabe',
  '1': '1 Anschluss',
  '2': '2 Anschlüsse',
  '3_PLUS': '3 oder mehr',
};

/** Labels of the power classes of GET /api/v1/stats. */
export const POWER_CLASSES: Record<string, string> = {
  UP_TO_22: 'bis 22 kW',
  FROM_23_TO_49: '23–49 kW',
  FROM_50_TO_149: '50–149 kW',
  FROM_150: 'ab 150 kW',
  UNKNOWN: 'unbekannt',
};

/** Counts of the charging infrastructure (evse:read). */
@Injectable({ providedIn: 'root' })
export class StatsService {
  private readonly http = inject(HttpClient);

  /** All regions, or the one with this ISO 3166-2 code. */
  load(region: string | null): Promise<StatsResponse> {
    return firstValueFrom(this.http.get<StatsResponse>('/api/v1/stats', { params: region ? { region } : {} }));
  }
}
