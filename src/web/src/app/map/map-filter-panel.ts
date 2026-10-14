import { Component, DestroyRef, ElementRef, computed, inject, output, signal, viewChild } from '@angular/core';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipListboxChange, MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSliderModule } from '@angular/material/slider';
import { AppIcon } from '../design/icon';
import { CONNECTORS, FILTER_FLAGS, FilterFlag, MapFilterStore, MapFilters, NO_FILTERS, POWER_STOPS } from './map-filters';
import { OperatorSummary, OperatorsService } from './operators.service';

/** Waiting time after the last key stroke in the operator search. */
export const OPERATOR_DELAY_MS = 300;

const LAST_STOP = POWER_STOPS.length - 1;

/** Slider position of a power: the first stop at or above it. */
function stopOf(power: number | null, fallback: number): number {
  if (power === null) {
    return fallback;
  }
  const index = POWER_STOPS.findIndex((stop) => stop >= power);
  return index === -1 ? LAST_STOP : index;
}

/**
 * Filters of the map (loaded on demand when opened): connectors, power range, operators and the flags. Every change
 * applies at once; the map loads its locations again. Operators come from GET /api/v1/operators, most locations first,
 * with a search over name and operator ID.
 */
@Component({
  imports: [AppIcon, MatAutocompleteModule, MatButtonModule, MatCardModule, MatChipsModule, MatFormFieldModule, MatInputModule, MatSliderModule],
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

        <h3 id="filter-power-label">Leistung: <span id="power-range">{{ powerLabel() }}</span></h3>
        <mat-slider id="filter-power" min="0" [max]="lastStop" step="1" discrete [displayWith]="stopLabel" aria-labelledby="filter-power-label">
          <input matSliderStartThumb id="power-min" aria-label="Mindestleistung" [value]="minStop()" (valueChange)="power($event, maxStop())" />
          <input matSliderEndThumb id="power-max" aria-label="Höchstleistung" [value]="maxStop()" (valueChange)="power(minStop(), $event)" />
        </mat-slider>

        <h3 id="filter-more-label">Eigenschaften</h3>
        <mat-chip-listbox multiple id="filter-flags" aria-labelledby="filter-more-label" (change)="flags($event)">
          @for (flag of flagOptions; track flag.key) {
            <mat-chip-option [value]="flag.key" [selected]="filters()[flag.key]">{{ flag.label }}</mat-chip-option>
          }
        </mat-chip-listbox>

        <h3 id="filter-operator-label">Betreiber</h3>
        @if (filters().operatorId.length > 0) {
          <mat-chip-set id="filter-operators" aria-labelledby="filter-operator-label">
            @for (id of filters().operatorId; track id) {
              <mat-chip [removable]="true" (removed)="removeOperator(id)">
                {{ names()[id] ?? 'Betreiber' }}
                <button matChipRemove [attr.aria-label]="(names()[id] ?? 'Betreiber') + ' entfernen'"><app-icon name="close" /></button>
              </mat-chip>
            }
          </mat-chip-set>
        }
        <mat-form-field subscriptSizing="dynamic" class="filter-operator">
          <mat-label>Betreiber suchen</mat-label>
          <input
            #operatorInput
            matInput
            id="filter-operator"
            maxlength="100"
            autocomplete="off"
            [matAutocomplete]="operatorHits"
            (focus)="loadOperators()"
            (input)="typeOperator($event)"
          />
          <mat-autocomplete #operatorHits="matAutocomplete" (optionSelected)="addOperator($event)">
            @for (item of operators(); track item.id) {
              <mat-option [value]="item" [disabled]="filters().operatorId.includes(item.id)">
                <span class="hit-label">{{ item.name }}</span>
                <span class="hit-detail">{{ item.locations }} {{ item.locations === 1 ? 'Standort' : 'Standorte' }}</span>
              </mat-option>
            } @empty {
              <mat-option disabled>{{ operatorsFailed() ? 'Die Betreiber sind gerade nicht erreichbar.' : 'Kein Betreiber gefunden.' }}</mat-option>
            }
          </mat-autocomplete>
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
  private readonly operatorsService = inject(OperatorsService);
  private readonly operatorInput = viewChild.required<ElementRef<HTMLInputElement>>('operatorInput');
  private timer: ReturnType<typeof setTimeout> | undefined;
  /** Number of the newest operator search; older answers are dropped. */
  private search = 0;

  readonly closed = output<void>();

  protected readonly filters = this.store.filters;
  protected readonly connectorOptions = CONNECTORS;
  protected readonly flagOptions = FILTER_FLAGS;
  protected readonly lastStop = LAST_STOP;
  protected readonly operators = signal<OperatorSummary[]>([]);
  protected readonly operatorsFailed = signal(false);
  /** Names of the selected operators by id. */
  protected readonly names = signal<Record<string, string>>({});
  protected readonly minStop = computed(() => stopOf(this.filters().minPowerKw, 0));
  protected readonly maxStop = computed(() => stopOf(this.filters().maxPowerKw, LAST_STOP));
  protected readonly stopLabel = (stop: number): string => (stop === 0 ? '0' : stop === LAST_STOP ? `${POWER_STOPS[stop]}+` : `${POWER_STOPS[stop]}`);
  protected readonly powerLabel = computed(() => {
    const { minPowerKw: min, maxPowerKw: max } = this.filters();
    const kw = (value: number) => `${String(value).replace('.', ',')} kW`;
    return min === null && max === null ? 'beliebig' : max === null ? `ab ${kw(min!)}` : min === null ? `bis ${kw(max)}` : `${kw(min)} bis ${kw(max)}`;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
    const selected = this.filters().operatorId;
    if (selected.length > 0) {
      void this.operatorsService
        .byIds(selected)
        .then((operators) => this.remember(operators))
        .catch(() => undefined);
    }
  }

  protected connectors(event: MatChipListboxChange): void {
    this.update({ connector: event.value as string[] });
  }

  /** Slider positions to the power range; the outer stops leave the range open. */
  protected power(minStop: number, maxStop: number): void {
    this.update({
      minPowerKw: minStop === 0 ? null : POWER_STOPS[minStop],
      maxPowerKw: maxStop === LAST_STOP ? null : POWER_STOPS[maxStop],
    });
  }

  protected flags(event: MatChipListboxChange): void {
    const selected = event.value as FilterFlag[];
    this.update(Object.fromEntries(FILTER_FLAGS.map(({ key }) => [key, selected.includes(key)])));
  }

  protected loadOperators(query = ''): void {
    void this.findOperators(query);
  }

  protected typeOperator(event: Event): void {
    const query = (event.target as HTMLInputElement).value.trim();
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.loadOperators(query), OPERATOR_DELAY_MS);
  }

  protected addOperator(event: MatAutocompleteSelectedEvent): void {
    const operator = event.option.value as OperatorSummary;
    this.remember([operator]);
    this.update({ operatorId: [...this.filters().operatorId, operator.id] });
    event.option.deselect();
    // The field is for searching only; the choice shows as a chip.
    this.operatorInput().nativeElement.value = '';
  }

  protected removeOperator(id: string): void {
    this.update({ operatorId: this.filters().operatorId.filter((item) => item !== id) });
  }

  protected reset(): void {
    clearTimeout(this.timer);
    this.store.set(NO_FILTERS);
  }

  private async findOperators(query: string): Promise<void> {
    const search = ++this.search;
    try {
      const operators = await this.operatorsService.search(query);
      if (search === this.search) {
        this.operators.set(operators);
        this.operatorsFailed.set(false);
      }
    } catch {
      if (search === this.search) {
        this.operators.set([]);
        this.operatorsFailed.set(true);
      }
    }
  }

  private remember(operators: OperatorSummary[]): void {
    this.names.update((names) => ({ ...names, ...Object.fromEntries(operators.map((item) => [item.id, item.name])) }));
  }

  private update(change: Partial<MapFilters>): void {
    this.store.set({ ...this.store.filters(), ...change });
  }
}
