import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { SourceAttribution } from '../map/locations.service';

/** Element of GET /api/v1/search. */
export interface SearchHit {
  type: 'EVSE' | 'LOCATION';
  label: string;
  detail: string | null;
  locationId: string;
  latitude: number;
  longitude: number;
  evseId: string | null;
  /** How free the location is now (as in GET /locations). */
  availability: 'AVAILABLE' | 'OCCUPIED' | 'OUT_OF_ORDER' | 'UNKNOWN';
  evsesTotal: number;
  evsesAvailable: number;
  evsesOccupied: number;
  /** For an EVSE hit its current OCPI status. */
  evseStatus: string | null;
}

/** Element of GET /api/v1/geocode: an Austrian address. */
export interface AddressHit {
  /** E.g. "Hauptplatz 1, 4020 Linz". */
  label: string;
  latitude: number;
  longitude: number;
}

/** The addresses of a query and the attribution of the address register. */
export interface AddressResult {
  hits: AddressHit[];
  attribution: SourceAttribution[];
}

/** Shortest query the API answers. */
export const MIN_QUERY = 2;

/** Shortest query the address search answers. */
export const MIN_ADDRESS_QUERY = 3;

/** Client of the public search. */
@Injectable({ providedIn: 'root' })
export class SearchService {
  private readonly http = inject(HttpClient);

  async search(query: string): Promise<SearchHit[]> {
    const response = await firstValueFrom(this.http.get<{ data: SearchHit[] }>('/api/v1/search', { params: { q: query, limit: 8 } }));
    return response.data;
  }

  /** Addresses for start and destination (route:read), the nearest to <code>near</code> first. */
  async geocode(query: string, near: { latitude: number; longitude: number } | null): Promise<AddressResult> {
    const params: Record<string, string> = { q: query };
    if (near) {
      params['near'] = `${near.latitude.toFixed(4)},${near.longitude.toFixed(4)}`;
    }
    const response = await firstValueFrom(
      this.http.get<{ data: AddressHit[]; meta: { attribution: SourceAttribution[] } }>('/api/v1/geocode', { params }),
    );
    return { hits: response.data, attribution: response.meta.attribution };
  }
}

/** A location the map should show: center on it and open its info card. */
export interface MapTarget {
  locationId: string;
  latitude: number;
  longitude: number;
}

/** Hands a search result to the map page, also across a navigation. */
@Injectable({ providedIn: 'root' })
export class MapFocus {
  readonly target = signal<MapTarget | null>(null);

  focus(target: MapTarget): void {
    this.target.set(target);
  }
}
