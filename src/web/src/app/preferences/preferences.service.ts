import { HttpClient } from '@angular/common/http';
import { DestroyRef, Injectable, computed, effect, inject, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { MapFilterStore, MapFilters, NO_FILTERS } from '../map/map-filters';
import { DEFAULT_VEHICLE, Vehicle, VehicleStore } from '../map/charging-plan';
import { DEFAULT_MAP_OPTIONS, MapOptions, MapOptionsStore } from '../map/map-options';
import { MapView, MapViewStore } from '../map/map-view.store';
import { CardsStore } from './cards.store';

/** Changes are written back after this pause, so a slider or several clicks cause one request. */
export const PREFERENCES_DEBOUNCE_MS = 1000;

/** The document of GET/PUT /api/v1/me/preferences. */
export interface PreferencesDocument {
  home: MapView | null;
  filters: Partial<MapFilters> | null;
  mapOptions: MapOptions | null;
  /** EMP keys of the user's charging cards and apps; missing or null: none. */
  cards?: string[] | null;
  /** The car of the charging plan; missing or null: the defaults. */
  vehicle?: Vehicle | null;
}

export interface PreferencesResponse {
  data: PreferencesDocument;
  meta: { updatedAt: string | null };
}

const URL = '/api/v1/me/preferences';

/** Filters of the server document; missing values count as "no filter". */
function toFilters(filters: Partial<MapFilters> | null): MapFilters {
  return { ...NO_FILTERS, ...Object.fromEntries(Object.entries(filters ?? {}).filter(([, value]) => value !== null)) };
}

/**
 * Keeps start position, filters, map options, own charging cards and the car of the charging plan of a signed-in user in the account (docs/06,
 * "Einstellungen"), so they follow the user across devices; anonymous visitors keep the map values in the browser only
 * and have no cards. After the login the account's
 * values replace the browser's, or, if the account has none yet, the browser's are uploaded. Every later change is
 * written back after a short pause. Filters of a shared link win over the account's and are not written to it unless
 * the user changes them. The last view stays per device.
 */
@Injectable({ providedIn: 'root' })
export class PreferencesService {
  private readonly http = inject(HttpClient);
  private readonly views = inject(MapViewStore);
  private readonly filters = inject(MapFilterStore);
  private readonly options = inject(MapOptionsStore);
  private readonly cards = inject(CardsStore);
  private readonly vehicles = inject(VehicleStore);
  /** Number of the current login; answers for an earlier one are dropped. */
  private session = 0;
  /** True once the account's values are known; changes before are not written. */
  private ready = false;
  /** The document as the server has it. */
  private synced: string | null = null;
  private timer: ReturnType<typeof setTimeout> | undefined;

  private readonly document = computed<PreferencesDocument>(() => ({
    home: this.views.home(),
    filters: this.filters.filters(),
    mapOptions: this.options.options(),
    cards: this.cards.cards(),
    vehicle: this.vehicles.vehicle(),
  }));

  constructor() {
    const auth = inject(AuthService);
    effect(() => {
      const signedIn = auth.isAuthenticated();
      untracked(() => (signedIn ? void this.start() : this.stop()));
    });
    effect(() => {
      const document = this.document();
      untracked(() => this.changed(document));
    });
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
  }

  private async start(): Promise<void> {
    const session = ++this.session;
    try {
      const response = await firstValueFrom(this.http.get<PreferencesResponse>(URL));
      if (session !== this.session) {
        return;
      }
      if (response.meta.updatedAt === null) {
        await this.put(this.document());
      } else {
        this.apply(response.data);
      }
      this.ready = true;
    } catch {
      // The browser keeps its values; the next login tries again.
    }
  }

  private stop(): void {
    this.session++;
    this.ready = false;
    this.synced = null;
    clearTimeout(this.timer);
    // The cards belong to the account that signed out.
    this.cards.set([]);
  }

  private apply(document: PreferencesDocument): void {
    if (document.home) {
      this.views.setHome(document.home);
    } else {
      this.views.resetHome();
    }
    if (!this.filters.fromLink) {
      this.filters.apply(toFilters(document.filters));
    }
    this.options.set(document.mapOptions ?? DEFAULT_MAP_OPTIONS);
    this.cards.set(document.cards ?? []);
    this.vehicles.set(document.vehicle ?? DEFAULT_VEHICLE);
    this.synced = JSON.stringify(this.document());
  }

  private changed(document: PreferencesDocument): void {
    if (!this.ready || JSON.stringify(document) === this.synced) {
      return;
    }
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.put(document).catch(() => undefined), PREFERENCES_DEBOUNCE_MS);
  }

  private async put(document: PreferencesDocument): Promise<void> {
    await firstValueFrom(this.http.put<PreferencesResponse>(URL, document));
    this.synced = JSON.stringify(document);
  }
}
