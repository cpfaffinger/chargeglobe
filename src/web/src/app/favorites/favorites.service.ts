import { HttpClient } from '@angular/common/http';
import { Injectable, effect, inject, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { LocationSummary, SourceAttribution } from '../map/locations.service';

/** Element of GET /api/v1/me/favorites. */
export interface Favorite {
  location: LocationSummary;
  addedAt: string;
  /** No source lists the location any more; it is not on the map and has no details. */
  removed: boolean;
}

export interface FavoritesResponse {
  data: Favorite[];
  meta: { returned: number; max: number; attribution: SourceAttribution[] };
}

/**
 * Favorite locations of the signed-in user (docs/06, "Favoriten"). Keeps the ids for the star buttons; they are
 * loaded after the login and dropped after the logout.
 */
@Injectable({ providedIn: 'root' })
export class FavoritesService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly ids = signal<ReadonlySet<string>>(new Set());
  /** The request in flight; callers at the same time share it. */
  private pending: Promise<FavoritesResponse> | null = null;

  constructor() {
    effect(() => {
      const signedIn = this.auth.isAuthenticated();
      untracked(() => {
        if (signedIn) {
          this.list().catch(() => undefined);
        } else {
          this.ids.set(new Set());
        }
      });
    });
  }

  isFavorite(locationId: string): boolean {
    return this.ids().has(locationId);
  }

  list(): Promise<FavoritesResponse> {
    this.pending ??= firstValueFrom(this.http.get<FavoritesResponse>('/api/v1/me/favorites'))
      .then((response) => {
        this.ids.set(new Set(response.data.map((favorite) => favorite.location.id)));
        return response;
      })
      .finally(() => (this.pending = null));
    return this.pending;
  }

  async add(locationId: string): Promise<void> {
    await firstValueFrom(this.http.put(`/api/v1/me/favorites/${encodeURIComponent(locationId)}`, null));
    this.ids.update((ids) => new Set([...ids, locationId]));
  }

  async remove(locationId: string): Promise<void> {
    await firstValueFrom(this.http.delete(`/api/v1/me/favorites/${encodeURIComponent(locationId)}`));
    this.ids.update((ids) => new Set([...ids].filter((id) => id !== locationId)));
  }
}
