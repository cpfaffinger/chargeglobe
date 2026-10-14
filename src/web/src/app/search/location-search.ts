import { Component, DestroyRef, inject, output, signal } from '@angular/core';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Router } from '@angular/router';
import { AppIcon } from '../design/icon';
import { MapFocus, MIN_QUERY, SearchHit, SearchService } from './search.service';

/** Waiting time after the last key stroke before the search runs. */
export const SEARCH_DELAY_MS = 250;

/**
 * Search field of the toolbar: charge points by EVSE-ID, locations by name or address. Choosing a hit shows the
 * location on the map (navigating there from other pages) and opens its info card.
 */
@Component({
  imports: [AppIcon, MatAutocompleteModule, MatFormFieldModule, MatInputModule],
  selector: 'app-location-search',
  template: `
    <mat-form-field subscriptSizing="dynamic" appearance="outline" class="location-search">
      <app-icon name="search" matPrefix />
      <input
        matInput
        type="search"
        id="location-search"
        placeholder="Ladepunkt-ID, Straße oder Ort"
        aria-label="Ladepunkt oder Adresse suchen"
        [value]="query()"
        [matAutocomplete]="hits"
        (input)="type($event)"
      />
      <mat-autocomplete #hits="matAutocomplete" (optionSelected)="choose($event)">
        @for (hit of results(); track hit.type + hit.label + hit.locationId) {
          <mat-option [value]="hit">
            <span class="hit-label">{{ hit.label }}</span>
            @if (hit.detail) {
              <span class="hit-detail">{{ hit.detail }}</span>
            }
          </mat-option>
        }
        @if (failed()) {
          <mat-option disabled>Die Suche ist gerade nicht erreichbar.</mat-option>
        } @else if (searched() && results().length === 0) {
          <mat-option disabled>Nichts gefunden.</mat-option>
        }
      </mat-autocomplete>
    </mat-form-field>
  `,
})
export class LocationSearch {
  private readonly search = inject(SearchService);
  private readonly focus = inject(MapFocus);
  private readonly router = inject(Router);
  private timer: ReturnType<typeof setTimeout> | undefined;
  /** Number of the newest search; older answers are dropped. */
  private request = 0;

  /** Emitted after a hit was chosen (e.g. to close the side sheet). */
  readonly chosen = output<SearchHit>();

  protected readonly query = signal('');
  protected readonly results = signal<SearchHit[]>([]);
  protected readonly searched = signal(false);
  protected readonly failed = signal(false);

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
  }

  protected type(event: Event): void {
    const query = (event.target as HTMLInputElement).value;
    this.query.set(query);
    clearTimeout(this.timer);
    if (query.trim().length < MIN_QUERY) {
      this.request++;
      this.results.set([]);
      this.searched.set(false);
      return;
    }

    this.timer = setTimeout(() => void this.run(query.trim()), SEARCH_DELAY_MS);
  }

  protected async choose(event: MatAutocompleteSelectedEvent): Promise<void> {
    const hit = event.option.value as SearchHit;
    this.query.set('');
    this.results.set([]);
    this.searched.set(false);
    this.focus.focus({ locationId: hit.locationId, latitude: hit.latitude, longitude: hit.longitude });
    this.chosen.emit(hit);
    if (this.router.url !== '/') {
      await this.router.navigateByUrl('/');
    }
  }

  private async run(query: string): Promise<void> {
    const request = ++this.request;
    try {
      const hits = await this.search.search(query);
      if (request === this.request) {
        this.results.set(hits);
        this.failed.set(false);
        this.searched.set(true);
      }
    } catch {
      if (request === this.request) {
        this.results.set([]);
        this.failed.set(true);
      }
    }
  }
}
