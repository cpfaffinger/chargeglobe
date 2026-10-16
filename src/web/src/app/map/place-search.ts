import { Component, DestroyRef, inject, input, output, signal } from '@angular/core';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { SEARCH_DELAY_MS } from '../search/location-search';
import { MIN_QUERY, SearchHit, SearchService } from '../search/search.service';
import { Place } from './routes.service';

/** The place of a search hit: its location (an EVSE hit by its EVSE-ID). */
export function hitPlace(hit: SearchHit): Place {
  return { latitude: hit.latitude, longitude: hit.longitude, name: hit.label, kind: 'location', locationId: hit.locationId };
}

/**
 * A search field for a start or destination of the route planner: the hits of GET /search (charging locations and
 * EVSEs; addresses without a location need a geocoder, see docs/09), 250 ms after the last key, older answers dropped.
 */
@Component({
  imports: [MatAutocompleteModule, MatFormFieldModule, MatInputModule],
  selector: 'app-place-search',
  template: `
    <mat-form-field appearance="outline" subscriptSizing="dynamic" class="place-search">
      <mat-label>{{ label() }}</mat-label>
      <input matInput type="search" [value]="query()" [matAutocomplete]="places" (input)="type($event)" />
      <mat-autocomplete #places="matAutocomplete" (optionSelected)="choose($event)">
        @for (hit of hits(); track hit.type + hit.label + hit.locationId) {
          <mat-option [value]="hit">
            <span class="hit-label">{{ hit.label }}</span>
            @if (hit.detail) {
              <span class="hit-detail">{{ hit.detail }}</span>
            }
          </mat-option>
        }
        @if (failed()) {
          <mat-option disabled>Die Suche ist gerade nicht erreichbar.</mat-option>
        } @else if (searched() && hits().length === 0) {
          <mat-option disabled>Nichts gefunden.</mat-option>
        }
      </mat-autocomplete>
    </mat-form-field>
  `,
})
export class PlaceSearch {
  private readonly search = inject(SearchService);
  private timer: ReturnType<typeof setTimeout> | undefined;
  /** Number of the newest search; older answers are dropped. */
  private request = 0;

  readonly label = input.required<string>();
  readonly picked = output<Place>();

  protected readonly query = signal('');
  protected readonly hits = signal<SearchHit[]>([]);
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
      this.hits.set([]);
      this.searched.set(false);
      return;
    }
    this.timer = setTimeout(() => void this.run(query.trim()), SEARCH_DELAY_MS);
  }

  protected choose(event: MatAutocompleteSelectedEvent): void {
    this.query.set('');
    this.hits.set([]);
    this.searched.set(false);
    this.picked.emit(hitPlace(event.option.value as SearchHit));
  }

  private async run(query: string): Promise<void> {
    const request = ++this.request;
    try {
      const hits = await this.search.search(query);
      if (request === this.request) {
        this.hits.set(hits);
        this.failed.set(false);
        this.searched.set(true);
      }
    } catch {
      if (request === this.request) {
        this.hits.set([]);
        this.failed.set(true);
        this.searched.set(true);
      }
    }
  }
}
