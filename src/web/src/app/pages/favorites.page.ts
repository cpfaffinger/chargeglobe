import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { AppIcon } from '../design/icon';
import { AppLoading } from '../design/loading';
import { FavoriteButton } from '../favorites/favorite-button';
import { FavoritesResponse, FavoritesService } from '../favorites/favorites.service';
import { MARKERS, busyText, markerOf, tokenColor } from '../map/availability';
import { LocationSummary } from '../map/locations.service';

/** The list refreshes itself in this interval, as long as the page is open. */
export const FAVORITES_REFRESH_MS = 60_000;

/**
 * Favorite locations of the signed-in user (/favorites) with their current status, refreshed every minute: free
 * charge points, power, time of the status, and links to the map and the details.
 */
@Component({
  imports: [AppLoading, AppIcon, DatePipe, DecimalPipe, FavoriteButton, MatButtonModule, MatCardModule, RouterLink],
  selector: 'app-favorites',
  template: `
    <div class="favorites-head">
      <h1>Favoriten</h1>
      <button matButton type="button" id="refresh-favorites" [disabled]="loading()" (click)="load()"><app-icon name="refresh" />Aktualisieren</button>
    </div>
    @if (loading() && !response()) {
      <app-loading id="favorites-loading" text="Favoriten werden geladen …" />
    }
    @if (failed()) {
      <p class="error" role="alert" id="favorites-error">Die Favoriten konnten nicht geladen werden. Bitte versuche es später erneut.</p>
    }
    @if (response(); as current) {
      <p class="lead" id="favorites-meta">
        Stand {{ loadedAt() | date: 'HH:mm' }} · aktualisiert sich jede Minute · {{ current.data.length }} von höchstens {{ current.meta.max }}
      </p>
      <div class="favorites-list">
        @for (favorite of current.data; track favorite.location.id) {
          @let location = favorite.location;
          <mat-card appearance="outlined" class="favorite" [attr.data-location]="location.id">
            <mat-card-content>
              <div class="card-head">
                <div>
                  <h2>{{ location.name ?? 'Ladestandort' }}</h2>
                  <p class="address">{{ location.address }}, {{ location.postalCode }} {{ location.city }}</p>
                </div>
                <app-favorite-button [locationId]="location.id" />
              </div>
              <p class="availability">
                <span class="status-dot" [style.background]="color(location)"><app-icon [name]="icon(location)" /></span>
                {{ location.evsesAvailable }} von {{ location.evsesTotal }} Ladepunkten frei{{ busy(location) }}
                @if (location.maxAvailablePowerKw !== null) {
                  · frei bis {{ location.maxAvailablePowerKw | number: '1.0-1' : 'en-US' }} kW
                } @else if (location.maxPowerKw !== null) {
                  · bis {{ location.maxPowerKw | number: '1.0-1' : 'en-US' }} kW
                }
              </p>
              <p class="status-age">
                @if (location.statusObservedAt) {
                  Status von {{ location.statusObservedAt | date: 'dd.MM.yyyy HH:mm' }}
                } @else {
                  Kein aktueller Status
                }
              </p>
              <p class="favorite-actions">
                <a matButton routerLink="/" [fragment]="mapFragment(location)" class="show-on-map">Auf der Karte</a>
                @if (canSeeDetails) {
                  <a matButton [routerLink]="['/locations', location.id]" class="details">Details</a>
                }
              </p>
            </mat-card-content>
          </mat-card>
        } @empty {
          <p id="no-favorites">
            Du hast noch keine Favoriten. Öffne auf der <a routerLink="/">Karte</a> einen Ladestandort und tippe auf den Stern.
          </p>
        }
      </div>
      @if (attribution().length > 0) {
        <p id="favorites-attribution">{{ attribution().join(' · ') }}</p>
      }
    }
  `,
})
export class FavoritesPage {
  protected readonly busy = busyText;
  private readonly favorites = inject(FavoritesService);

  protected readonly canSeeDetails = inject(AuthService).hasPermission('evse:read');
  protected readonly response = signal<FavoritesResponse | null>(null);
  protected readonly loadedAt = signal<Date | null>(null);
  protected readonly loading = signal(false);
  protected readonly failed = signal(false);
  protected readonly attribution = computed(() => [...new Set(this.response()?.meta.attribution.map((source) => source.text) ?? [])]);

  constructor() {
    void this.load();
    const timer = setInterval(() => void this.load(), FAVORITES_REFRESH_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  protected color(location: LocationSummary): string {
    return tokenColor(MARKERS[markerOf(location)].token);
  }

  protected icon(location: LocationSummary) {
    return MARKERS[markerOf(location)].icon;
  }

  protected mapFragment(location: LocationSummary): string {
    return `map=17.00/${location.latitude.toFixed(5)}/${location.longitude.toFixed(5)}`;
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.response.set(await this.favorites.list());
      this.loadedAt.set(new Date());
      this.failed.set(false);
    } catch {
      this.failed.set(true);
    } finally {
      this.loading.set(false);
    }
  }
}
