import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { AppLoading } from '../design/loading';
import { connectorLabel } from '../evses/labels';
import { CONNECTOR_SIZES, RegionCount, POWER_CLASSES, SITE_SIZES, StatsResponse, StatsService } from '../stats/stats.service';

/** One bar of a chart: label, value and its share of the largest value (0–100). */
interface Bar {
  key: string;
  label: string;
  value: number;
  width: number;
}

/** Bars scaled to the largest value; all 0 when everything is 0. */
export function bars(items: { key: string; label: string; value: number }[]): Bar[] {
  const largest = Math.max(0, ...items.map((item) => item.value));
  return items.map((item) => ({ ...item, width: largest === 0 ? 0 : (item.value / largest) * 100 }));
}

/** The status groups of the stacked bar with their colour tokens, in the order of the bar. */
const STATUS_GROUPS = [
  { key: 'available', label: 'frei', color: 'var(--cg-status-available)' },
  { key: 'occupied', label: 'belegt', color: 'var(--cg-status-occupied)' },
  { key: 'faulted', label: 'außer Betrieb', color: 'var(--cg-status-out-of-order)' },
  { key: 'unknown', label: 'unbekannt', color: 'var(--cg-status-unknown)' },
  { key: 'stale', label: 'keine aktuelle Meldung', color: 'var(--mat-sys-outline-variant)' },
] as const;

const number = (value: number, digits = 0): string => new Intl.NumberFormat('de-DE', { maximumFractionDigits: digits }).format(value);

/**
 * Counts of the charging infrastructure (/stats, evse:read): tiles with the totals, bars per region, power class and
 * connector, the operators with most EVSEs, the current status and how fresh it is; for all regions or one.
 */
@Component({
  imports: [AppLoading, DatePipe, MatCardModule, MatFormFieldModule, MatProgressSpinnerModule, MatSelectModule, NgTemplateOutlet],
  selector: 'app-stats',
  template: `
    <ng-template #chart let-items>
      <ul class="bars">
        @for (bar of items; track bar.key) {
          <li [attr.data-key]="bar.key">
            <span class="bar-label">{{ bar.label }}</span>
            <span class="bar-track"><span class="bar-fill" [style.width.%]="bar.width"></span></span>
            <span class="bar-value">{{ format(bar.value) }}</span>
          </li>
        } @empty {
          <li class="muted">Keine Daten.</li>
        }
      </ul>
    </ng-template>

    <div class="stats-head">
      <h1>Statistik</h1>
      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="stats-region">
        <mat-label>Bundesland</mat-label>
        <mat-select id="stats-region" [value]="region()" (selectionChange)="choose($event.value)">
          <mat-option [value]="null">Alle</mat-option>
          @for (item of regions(); track item.code) {
            <mat-option [value]="item.code">{{ item.name }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
    </div>
    @if (loading()) {
      <app-loading id="stats-loading" text="Statistik wird geladen …" />
    }
    @if (failed()) {
      <p class="error" role="alert" id="stats-error">Die Statistik konnte nicht geladen werden. Bitte versuche es später erneut.</p>
    }
    @if (stats(); as response) {
      @let data = response.data;
      <div class="stats-tiles">
        <div class="tile" id="tile-locations"><span class="tile-value">{{ format(data.totals.locations) }}</span><span class="tile-label">Ladestandorte</span></div>
        <div class="tile" id="tile-parks"><span class="tile-value">{{ format(data.totals.parks) }}</span><span class="tile-label">Ladeparks</span></div>
        <div class="tile" id="tile-stations"><span class="tile-value">{{ format(data.totals.stations) }}</span><span class="tile-label">Ladestationen bekannt</span></div>
        <div class="tile" id="tile-evses"><span class="tile-value">{{ format(data.totals.evses) }}</span><span class="tile-label">Ladepunkte</span></div>
        <div class="tile" id="tile-connectors"><span class="tile-value">{{ format(data.totals.connectors) }}</span><span class="tile-label">Anschlüsse</span></div>
        <div class="tile" id="tile-available"><span class="tile-value">{{ format(data.status.available) }}</span><span class="tile-label">gerade frei</span></div>
      </div>
      <div class="stats-grid">
        @if (!data.region) {
          <mat-card appearance="outlined" id="by-region">
            <mat-card-content>
              <h2>Ladepunkte je Bundesland</h2>
              <ng-container [ngTemplateOutlet]="chart" [ngTemplateOutletContext]="{ $implicit: regionBars() }" />
            </mat-card-content>
          </mat-card>
        }
        <mat-card appearance="outlined" id="by-power">
          <mat-card-content>
            <h2>Ladepunkte nach Leistung</h2>
            <ng-container [ngTemplateOutlet]="chart" [ngTemplateOutletContext]="{ $implicit: powerBars() }" />
          </mat-card-content>
        </mat-card>
        <mat-card appearance="outlined" id="per-site">
          <mat-card-content>
            <h2>Ladepunkte je Standort oder Ladepark</h2>
            <p class="lead">
              Ein Ladepunkt lädt genau ein Fahrzeug zugleich. Standorte desselben Betreibers direkt nebeneinander zählen als ein
              Ladepark.
            </p>
            <ng-container [ngTemplateOutlet]="chart" [ngTemplateOutletContext]="{ $implicit: siteBars() }" />
          </mat-card-content>
        </mat-card>
        <mat-card appearance="outlined" id="per-evse">
          <mat-card-content>
            <h2>Anschlüsse je Ladepunkt</h2>
            <p class="lead">Mehrere Anschlüsse (Stecker oder Kabel) an einem Ladepunkt sind Alternativen, kein zweites Fahrzeug.</p>
            <ng-container [ngTemplateOutlet]="chart" [ngTemplateOutletContext]="{ $implicit: connectorSizeBars() }" />
          </mat-card-content>
        </mat-card>
        <mat-card appearance="outlined" id="by-connector">
          <mat-card-content>
            <h2>Anschlüsse nach Typ</h2>
            <ng-container [ngTemplateOutlet]="chart" [ngTemplateOutletContext]="{ $implicit: connectorBars() }" />
          </mat-card-content>
        </mat-card>
        <mat-card appearance="outlined" id="top-operators">
          <mat-card-content>
            <h2>Betreiber mit den meisten Ladepunkten</h2>
            <ng-container [ngTemplateOutlet]="chart" [ngTemplateOutletContext]="{ $implicit: operatorBars() }" />
          </mat-card-content>
        </mat-card>
        <mat-card appearance="outlined" id="status">
          <mat-card-content>
            <h2>Aktueller Status</h2>
            <div class="status-bar" role="img" [attr.aria-label]="statusText()">
              @for (part of statusParts(); track part.key) {
                <span [style.width.%]="part.share" [style.background]="part.color" [title]="part.label + ': ' + format(part.value)"></span>
              }
            </div>
            <ul class="status-legend">
              @for (part of statusParts(); track part.key) {
                <li [attr.data-key]="part.key"><span class="swatch" [style.background]="part.color"></span>{{ part.label }}: {{ format(part.value) }}</li>
              }
            </ul>
            <p class="lead" id="freshness">
              @if (data.freshness.p50Minutes !== null) {
                Alter der Statusmeldungen: Median {{ format(data.freshness.p50Minutes, 1) }} min, 90 % jünger als {{ format(data.freshness.p90Minutes!, 1) }} min.
              } @else {
                Keine Statusmeldungen.
              }
            </p>
          </mat-card-content>
        </mat-card>
      </div>
      <p class="lead" id="stats-generated">Stand {{ data.generatedAt | date: 'dd.MM.yyyy HH:mm' }}</p>
      @if (response.meta.attribution.length > 0) {
        <p id="stats-attribution">{{ attribution() }}</p>
      }
    }
  `,
})
export class StatsPage {
  private readonly service = inject(StatsService);
  protected readonly region = signal<string | null>(null);
  protected readonly stats = signal<StatsResponse | null>(null);
  protected readonly loading = signal(false);
  protected readonly failed = signal(false);
  /** The regions of the selection, from the counts of all regions. */
  protected readonly regions = signal<RegionCount[]>([]);
  /** Answers for an earlier selection are dropped. */
  private request = 0;

  protected readonly regionBars = computed(() =>
    bars((this.stats()?.data.byRegion ?? []).map((item) => ({ key: item.code, label: item.name, value: item.evses }))),
  );
  protected readonly powerBars = computed(() =>
    bars((this.stats()?.data.byPowerClass ?? []).map((item) => ({ key: item.class, label: POWER_CLASSES[item.class] ?? item.class, value: item.evses }))),
  );
  protected readonly connectorBars = computed(() =>
    bars((this.stats()?.data.byConnector ?? []).map((item) => ({ key: item.standard, label: connectorLabel(item.standard), value: item.connectors }))),
  );
  protected readonly siteBars = computed(() =>
    bars((this.stats()?.data.evsesPerSite ?? []).map((item) => ({ key: item.size, label: SITE_SIZES[item.size] ?? item.size, value: item.count }))),
  );
  protected readonly connectorSizeBars = computed(() =>
    bars(
      (this.stats()?.data.connectorsPerEvse ?? []).map((item) => ({ key: item.size, label: CONNECTOR_SIZES[item.size] ?? item.size, value: item.count })),
    ),
  );
  protected readonly operatorBars = computed(() =>
    bars((this.stats()?.data.topOperators ?? []).map((item) => ({ key: item.id, label: item.name, value: item.evses }))),
  );
  protected readonly statusParts = computed(() => {
    const status = this.stats()?.data.status;
    const total = status ? Object.values(status).reduce((sum, value) => sum + value, 0) : 0;
    return STATUS_GROUPS.map((group) => {
      const value = status?.[group.key] ?? 0;
      return { ...group, value, share: total === 0 ? 0 : (value / total) * 100 };
    });
  });
  protected readonly statusText = computed(() => this.statusParts().map((part) => `${part.label}: ${number(part.value)}`).join(', '));
  protected readonly attribution = computed(() => (this.stats()?.meta.attribution ?? []).map((item) => item.text).join(' · '));

  constructor() {
    void this.load();
  }

  protected format(value: number, digits = 0): string {
    return number(value, digits);
  }

  protected choose(region: string | null): void {
    this.region.set(region);
    void this.load();
  }

  private async load(): Promise<void> {
    const request = ++this.request;
    const region = this.region();
    this.loading.set(true);
    this.failed.set(false);
    try {
      const response = await this.service.load(region);
      if (request === this.request) {
        this.stats.set(response);
        if (region === null) {
          this.regions.set(response.data.byRegion);
        }
      }
    } catch {
      if (request === this.request) {
        this.failed.set(true);
      }
    } finally {
      if (request === this.request) {
        this.loading.set(false);
      }
    }
  }
}
