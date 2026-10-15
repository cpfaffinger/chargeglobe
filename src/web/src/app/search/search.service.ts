import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

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

/** Shortest query the API answers. */
export const MIN_QUERY = 2;

/** Client of the public search. */
@Injectable({ providedIn: 'root' })
export class SearchService {
  private readonly http = inject(HttpClient);

  async search(query: string): Promise<SearchHit[]> {
    const response = await firstValueFrom(this.http.get<{ data: SearchHit[] }>('/api/v1/search', { params: { q: query, limit: 8 } }));
    return response.data;
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
