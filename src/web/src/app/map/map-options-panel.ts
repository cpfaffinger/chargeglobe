import { Component, inject, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatSliderModule } from '@angular/material/slider';
import { AppIcon } from '../design/icon';
import { CLUSTER_MAX_ZOOM, CLUSTER_RADIUS, DEFAULT_MAP_OPTIONS, MapOptionsStore } from './map-options';

/** "Kartenoptionen" (loaded on demand when opened): how locations are combined, with "Standard". Changes apply at once. */
@Component({
  imports: [AppIcon, MatButtonModule, MatCardModule, MatSliderModule],
  selector: 'app-map-options-panel',
  template: `
    <mat-card id="map-options" appearance="outlined" role="region" aria-label="Kartenoptionen">
      <mat-card-content>
        <div class="card-head">
          <h2>Kartenoptionen</h2>
          <button matIconButton type="button" id="close-options" aria-label="Kartenoptionen schließen" (click)="closed.emit()">
            <app-icon name="close" />
          </button>
        </div>

        <h3 id="cluster-radius-label">
          Ladepunkte zusammenfassen:
          <span id="cluster-radius-value">{{ options().clusterRadius === 0 ? 'nie' : 'im Umkreis von ' + options().clusterRadius + ' px' }}</span>
        </h3>
        <mat-slider [min]="radius.min" [max]="radius.max" [step]="radius.step" discrete aria-labelledby="cluster-radius-label">
          <input matSliderThumb id="cluster-radius" [value]="options().clusterRadius" (valueChange)="store.set({ clusterRadius: $event })" />
        </mat-slider>
        <p class="hint">Je größer der Umkreis, desto mehr nahe Ladepunkte werden zu einer Zahl zusammengefasst; 0 zeigt alle einzeln.</p>

        <h3 id="cluster-zoom-label">
          Zusammenfassen bis Zoomstufe <span id="cluster-zoom-value">{{ options().clusterMaxZoom }}</span>
        </h3>
        <mat-slider [min]="zoom.min" [max]="zoom.max" [step]="zoom.step" discrete [disabled]="options().clusterRadius === 0" aria-labelledby="cluster-zoom-label">
          <input matSliderThumb id="cluster-zoom" [value]="options().clusterMaxZoom" (valueChange)="store.set({ clusterMaxZoom: $event })" />
        </mat-slider>
        <p class="hint">Weiter hineingezoomt erscheint jeder Ladestandort einzeln (Standard: Zoomstufe {{ defaults.clusterMaxZoom }}, {{ defaults.clusterRadius }} px).</p>

        <div class="filter-actions">
          <button matButton type="button" id="reset-options" [disabled]="store.isDefault()" (click)="store.reset()">Auf Standard zurücksetzen</button>
          <button matButton="filled" type="button" id="apply-options" (click)="closed.emit()">Fertig</button>
        </div>
      </mat-card-content>
    </mat-card>
  `,
})
export class MapOptionsPanel {
  protected readonly store = inject(MapOptionsStore);

  readonly closed = output<void>();

  protected readonly options = this.store.options;
  protected readonly radius = CLUSTER_RADIUS;
  protected readonly zoom = CLUSTER_MAX_ZOOM;
  protected readonly defaults = DEFAULT_MAP_OPTIONS;
}
