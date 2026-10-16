import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { MapFilters, NO_FILTERS, filterParams } from './map-filters';

/** Visible area of the map in WGS 84. */
export interface Bbox {
  west: number;
  south: number;
  east: number;
  north: number;
}

export type Availability = 'AVAILABLE' | 'OCCUPIED' | 'OUT_OF_ORDER' | 'UNKNOWN';

/** Element of GET /api/v1/locations. */
export interface LocationSummary {
  id: string;
  name: string | null;
  address: string | null;
  postalCode: string | null;
  city: string | null;
  country: string;
  latitude: number;
  longitude: number;
  operator: string | null;
  evsesTotal: number;
  evsesAvailable: number;
  maxPowerKw: number | null;
  statusObservedAt: string | null;
  availability: Availability;
  /** Highest power among the EVSEs that are free now; null if none is. */
  maxAvailablePowerKw: number | null;
  /** EVSEs charging, blocked or reserved now. */
  evsesOccupied: number;
  /** EVSEs out of order now; the rest of evsesTotal has no current status. */
  evsesFaulted: number;
  /** Charging park of locations of one operator next to each other; null if the location stands alone. */
  parkId: string | null;
  /** Locations in its park, itself included; 1 if alone. */
  parkLocations: number;
}

export interface SourceAttribution {
  source: string;
  text: string;
  license: string;
}

export interface LocationsResponse {
  data: LocationSummary[];
  meta: { returned: number; limit: number; truncated: boolean; attribution: SourceAttribution[] };
}

/** Client of the public location endpoints (no login required). */
@Injectable({ providedIn: 'root' })
export class LocationsService {
  /** Maximum of the API; the map clusters the markers itself. */
  static readonly limit = 2000;

  private readonly http = inject(HttpClient);

  /** Locations in the box that match the filters (GET /api/v1/locations). */
  inBox(box: Bbox, filters: MapFilters = NO_FILTERS): Promise<LocationsResponse> {
    const bbox = [box.west, box.south, box.east, box.north].map((value) => value.toFixed(5)).join(',');
    return firstValueFrom(
      this.http.get<LocationsResponse>('/api/v1/locations', {
        params: { bbox, limit: LocationsService.limit, ...filterParams(filters) },
      }),
    );
  }
}
