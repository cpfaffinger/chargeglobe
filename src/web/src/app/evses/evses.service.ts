import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { LocationSummary, SourceAttribution } from '../map/locations.service';

export interface ConnectorDetail {
  standard: string;
  format: string | null;
  powerType: string | null;
  maxPowerKw: number | null;
}

/** EVSE of GET /api/v1/locations/{id}. */
export interface EvseDetail {
  id: string;
  evseId: string;
  hasEvseId: boolean;
  /** OCPI status; UNKNOWN if missing or older than map.status_max_age_minutes. */
  status: string;
  statusSince: string | null;
  statusObservedAt: string | null;
  maxPowerKw: number | null;
  powerType: string | null;
  connectors: ConnectorDetail[];
}

export interface LocationDetailResponse {
  data: { location: LocationSummary; evses: EvseDetail[] };
  meta: { attribution: SourceAttribution[] };
}

/** Period with constant status; `to` is null while it lasts. */
export interface StatusInterval {
  status: string;
  from: string;
  to: string | null;
  source: string;
  confidence: number;
}

export interface StatusHistoryResponse {
  data: { id: string; evseId: string; from: string; to: string; intervals: StatusInterval[] };
  meta: { attribution: SourceAttribution[] };
}

/** One row of the occupancy matrix: 24 hours of one weekday (1 = Monday). */
export interface OccupancyDay {
  weekday: number;
  day: string;
  chargingShare: (number | null)[];
  observations: number[];
}

export interface Occupancy {
  id: string;
  evseId: string;
  weeks: number;
  from: string;
  to: string;
  timeZone: string;
  utilization: number | null;
  observations: number;
  days: OccupancyDay[];
}

export interface OccupancyResponse {
  data: Occupancy;
  meta: { attribution: SourceAttribution[] };
}

/**
 * Client of the location detail (public) and the EVSE endpoints, which need the permission evse:read
 * (docs/06-api.md, "Ladepunkte").
 */
@Injectable({ providedIn: 'root' })
export class EvsesService {
  private readonly http = inject(HttpClient);

  location(id: string): Promise<LocationDetailResponse> {
    return firstValueFrom(
      this.http.get<LocationDetailResponse>(`/api/v1/locations/${encodeURIComponent(id)}`),
    );
  }

  history(evseId: string, from: Date, to: Date): Promise<StatusHistoryResponse> {
    return firstValueFrom(
      this.http.get<StatusHistoryResponse>(
        `/api/v1/evses/${encodeURIComponent(evseId)}/status/history`,
        {
          params: { from: from.toISOString(), to: to.toISOString() },
        },
      ),
    );
  }

  occupancy(evseId: string, weeks: number): Promise<OccupancyResponse> {
    return firstValueFrom(
      this.http.get<OccupancyResponse>(`/api/v1/evses/${encodeURIComponent(evseId)}/occupancy`, {
        params: { weeks },
      }),
    );
  }
}
