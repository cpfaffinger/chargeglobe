import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { LocationSummary, SourceAttribution } from '../map/locations.service';
import { EvsePricesResponse } from './prices';

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

/** Facilities of a location; each value null if no source knows it. */
export interface LocationFacilities {
  greenEnergy: boolean | null;
  freeParking: boolean | null;
  roofedParking: boolean | null;
  barrierFreeParkingSpaces: number | null;
  priceUrl: string | null;
}

/** GET /api/v1/evses/{evseId}: master data, current status and the additional data of the sources. */
export interface EvseView {
  id: string;
  evseId: string;
  hasEvseId: boolean;
  lifecycle: string;
  location: {
    id: string;
    name: string | null;
    address: string | null;
    postalCode: string | null;
    city: string | null;
    country: string;
    latitude: number;
    longitude: number;
    region: string | null;
    access: string;
    openingTimes: { twentyFourSeven: boolean; description: string | null } | null;
    facilities: LocationFacilities;
  };
  operator: { id: string; name: string; operatorId: string | null } | null;
  physicalReference: string | null;
  maxPowerKw: number | null;
  powerType: string | null;
  connectors: ConnectorDetail[];
  authMethods: string[];
  roaming: boolean | null;
  status: { status: string; since: string | null; observedAt: string | null; source: string | null; stale: boolean };
  firstSeen: string;
  lastSeen: string;
  lastConfirmedAt: string | null;
  sources: { source: string; entity: string; externalId: string; firstSeen: string; lastSeen: string }[];
  provenance: { entity: string; attribute: string; source: string; observedAt: string }[];
}

export interface EvseResponse {
  data: EvseView;
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

  evse(evseId: string): Promise<EvseResponse> {
    return firstValueFrom(this.http.get<EvseResponse>(`/api/v1/evses/${encodeURIComponent(evseId)}`));
  }

  /** Ad-hoc price and its history (price:read). */
  prices(evseId: string): Promise<EvsePricesResponse> {
    return firstValueFrom(this.http.get<EvsePricesResponse>(`/api/v1/evses/${encodeURIComponent(evseId)}/prices`));
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
