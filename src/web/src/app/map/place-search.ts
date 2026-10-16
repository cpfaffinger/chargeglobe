import { Component, DestroyRef, inject, input, output, signal } from '@angular/core';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { SEARCH_DELAY_MS } from '../search/location-search';
import { AddressHit, AddressResult, MIN_ADDRESS_QUERY, MIN_QUERY, SearchHit, SearchService } from '../search/search.service';
import { SourceAttribution } from './locations.service';
import { Position, Place } from './routes.service';

/** The place of a search hit: its location (an EVSE hit by its EVSE-ID). */
export function hitPlace(hit: SearchHit): Place {
  return { latitude: hit.latitude, longitude: hit.longitude, name: hit.label, kind: 'location', locationId: hit.locationId };
}

/** The place of an address: a point named by the address. */
export function addressPlace(hit: AddressHit): Place {
  return { latitude: hit.latitude, longitude: hit.longitude, name: hit.label, kind: 'point', locationId: null };
}

/** An option of the field: a charging location or EVSE, or an address. */
type Choice = { location: SearchHit } | { address: AddressHit };

/**
 * A search field for a start or destination of the route planner: the hits of GET /search (charging locations and
 * EVSEs) and of GET /geocode (addresses, from three characters, the nearest to <code>near</code> first), asked in
 * parallel 250 ms after the last key; older answers are dropped, and one failing search leaves the other's hits.
 */
@Component({
  imports: [MatAutocompleteModule, MatFormFieldModule, MatInputModule],
  selector: 'app-place-search',
  template: `
    <mat-form-field appearance="outline" subscriptSizing="dynamic" class="place-search">
      <mat-label>{{ label() }}</mat-label>
      <input matInput type="search" [value]="query()" [matAutocomplete]="places" (input)="type($event)" />
      <mat-autocomplete #places="matAutocomplete" (optionSelected)="choose($event)">
        @if (hits().length > 0) {
          <mat-optgroup label="Ladestellen" class="hits-locations">
            @for (hit of hits(); track hit.type + hit.label + hit.locationId) {
              <mat-option [value]="{ location: hit }">
                <span class="hit-label">{{ hit.label }}</span>
                @if (hit.detail) {
                  <span class="hit-detail">{{ hit.detail }}</span>
                }
              </mat-option>
            }
          </mat-optgroup>
        }
        @if (addresses().length > 0) {
          <mat-optgroup label="Adressen" class="hits-addresses">
            @for (hit of addresses(); track hit.label + hit.latitude + hit.longitude) {
              <mat-option [value]="{ address: hit }">
                <span class="hit-label">{{ hit.label }}</span>
              </mat-option>
            }
            @for (source of attribution(); track source.source) {
              <mat-option disabled class="hit-attribution">{{ source.text }} ({{ source.license }})</mat-option>
            }
          </mat-optgroup>
        }
        @if (failed()) {
          <mat-option disabled>Die Suche ist gerade nicht erreichbar.</mat-option>
        } @else if (searched() && hits().length === 0 && addresses().length === 0) {
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
  /** Addresses near it come first, e.g. the map center. */
  readonly near = input<Position | null>(null);
  readonly picked = output<Place>();

  protected readonly query = signal('');
  protected readonly hits = signal<SearchHit[]>([]);
  protected readonly addresses = signal<AddressHit[]>([]);
  protected readonly attribution = signal<SourceAttribution[]>([]);
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
      this.clear();
      this.failed.set(false);
      return;
    }
    this.timer = setTimeout(() => void this.run(query.trim()), SEARCH_DELAY_MS);
  }

  protected choose(event: MatAutocompleteSelectedEvent): void {
    const choice = event.option.value as Choice;
    this.query.set('');
    this.clear();
    this.picked.emit('location' in choice ? hitPlace(choice.location) : addressPlace(choice.address));
  }

  private clear(): void {
    this.hits.set([]);
    this.addresses.set([]);
    this.attribution.set([]);
    this.searched.set(false);
  }

  private async run(query: string): Promise<void> {
    const request = ++this.request;
    const withAddresses = query.length >= MIN_ADDRESS_QUERY;
    const [locations, addresses] = await Promise.allSettled([
      this.search.search(query),
      withAddresses ? this.search.geocode(query, this.near()) : Promise.resolve<AddressResult>({ hits: [], attribution: [] }),
    ]);
    if (request !== this.request) {
      return;
    }
    this.hits.set(locations.status === 'fulfilled' ? locations.value : []);
    this.addresses.set(addresses.status === 'fulfilled' ? addresses.value.hits : []);
    this.attribution.set(addresses.status === 'fulfilled' ? addresses.value.attribution : []);
    this.failed.set(locations.status === 'rejected' && (!withAddresses || addresses.status === 'rejected'));
    this.searched.set(true);
  }
}
