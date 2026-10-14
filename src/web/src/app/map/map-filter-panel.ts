import { Component, DestroyRef, inject, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipListboxChange, MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { AppIcon } from '../design/icon';
import { CONNECTORS, FILTER_FLAGS, FilterFlag, MapFilterStore, MapFilters, NO_FILTERS, POWER_STEPS } from './map-filters';

/** Waiting time after the last key stroke in the operator field. */
export const OPERATOR_DELAY_MS = 400;

/**
 * Filters of the map (loaded on demand when opened): connectors, minimum power, operator and the flags. Every change
 * applies at once; the map loads its locations again.
 */
@Component({
  imports: [AppIcon, MatButtonModule, MatCardModule, MatChipsModule, MatFormFieldModule, MatInputModule],
  selector: 'app-map-filter-panel',
  template: `
    <mat-card id="filter-panel" appearance="outlined" role="region" aria-label="Filter">
      <mat-card-content>
        <div class="card-head">
          <h2>Filter</h2>
          <button matIconButton type="button" id="close-filters" aria-label="Filter schließen" (click)="closed.emit()">
            <app-icon name="close" />
          </button>
        </div>

        <h3 id="filter-connector-label">Stecker</h3>
        <mat-chip-listbox multiple id="filter-connector" aria-labelledby="filter-connector-label" (change)="connectors($event)">
          @for (item of connectorOptions; track item.standard) {
            <mat-chip-option [value]="item.standard" [selected]="filters().connector.includes(item.standard)">{{ item.label }}</mat-chip-option>
          }
        </mat-chip-listbox>

        <h3 id="filter-power-label">Leistung</h3>
        <mat-chip-listbox id="filter-power" aria-labelledby="filter-power-label" (change)="power($event)">
          @for (step of powerSteps; track step) {
            <mat-chip-option [value]="step" [selected]="filters().minPowerKw === step">ab {{ step }} kW</mat-chip-option>
          }
        </mat-chip-listbox>

        <h3 id="filter-more-label">Eigenschaften</h3>
        <mat-chip-listbox multiple id="filter-flags" aria-labelledby="filter-more-label" (change)="flags($event)">
          @for (flag of flagOptions; track flag.key) {
            <mat-chip-option [value]="flag.key" [selected]="filters()[flag.key]">{{ flag.label }}</mat-chip-option>
          }
        </mat-chip-listbox>

        <mat-form-field subscriptSizing="dynamic" class="filter-operator">
          <mat-label>Betreiber</mat-label>
          <input matInput id="filter-operator" maxlength="100" [value]="filters().operator" (input)="operator($event)" />
        </mat-form-field>

        <div class="filter-actions">
          <button matButton type="button" id="reset-filters" [disabled]="store.count() === 0" (click)="reset()">Filter zurücksetzen</button>
          <button matButton="filled" type="button" id="apply-filters" (click)="closed.emit()">Fertig</button>
        </div>
      </mat-card-content>
    </mat-card>
  `,
})
export class MapFilterPanel {
  protected readonly store = inject(MapFilterStore);
  private timer: ReturnType<typeof setTimeout> | undefined;

  readonly closed = output<void>();

  protected readonly filters = this.store.filters;
  protected readonly connectorOptions = CONNECTORS;
  protected readonly powerSteps = POWER_STEPS;
  protected readonly flagOptions = FILTER_FLAGS;

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
  }

  protected connectors(event: MatChipListboxChange): void {
    this.update({ connector: event.value as string[] });
  }

  protected power(event: MatChipListboxChange): void {
    this.update({ minPowerKw: (event.value as number | undefined) ?? null });
  }

  protected flags(event: MatChipListboxChange): void {
    const selected = event.value as FilterFlag[];
    this.update(Object.fromEntries(FILTER_FLAGS.map(({ key }) => [key, selected.includes(key)])));
  }

  protected operator(event: Event): void {
    const value = (event.target as HTMLInputElement).value.trim();
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.update({ operator: value }), OPERATOR_DELAY_MS);
  }

  protected reset(): void {
    clearTimeout(this.timer);
    this.store.set(NO_FILTERS);
  }

  private update(change: Partial<MapFilters>): void {
    this.store.set({ ...this.store.filters(), ...change });
  }
}
