import { DatePipe, DecimalPipe, PercentPipe, formatDate, formatPercent } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import {
  EvseDetail,
  EvsesService,
  LocationDetailResponse,
  Occupancy,
  OccupancyDay,
} from '../evses/evses.service';
import { Segment, combine, heatColor, statusOf, timeline } from '../evses/status';

/** Days of the status timeline and weeks of the occupancy matrix. */
const TIMELINE_DAYS = 7;
const OCCUPANCY_WEEKS = 4;
const DAY_LABELS = ['Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa', 'So'];

type LoadState = 'loading' | 'error' | 'ready';

/**
 * Detail of a charging location for signed-in users (guard with evse:read in app.routes.ts): the EVSEs with
 * status and connectors, the status timeline of the last 7 days per EVSE and a weekday × hour heatmap of the
 * share of readings with status CHARGING over the last 4 weeks.
 */
@Component({
  imports: [DatePipe, DecimalPipe, PercentPipe, RouterLink],
  selector: 'app-location-detail',
  template: `
    <p><a routerLink="/" id="back">← Zur Karte</a></p>
    @if (notice() === 'missing') {
      <p id="detail-notice" class="error">Diesen Ladestandort gibt es nicht oder nicht mehr.</p>
    } @else if (notice() === 'failed') {
      <p id="detail-notice" class="error">
        Der Ladestandort konnte nicht geladen werden. Bitte versuche es später erneut.
      </p>
    }
    @if (detail(); as detail) {
      @let location = detail.data.location;
      <h2 id="location-name">{{ location.name ?? 'Ladestandort' }}</h2>
      <p class="address">{{ location.address }}, {{ location.postalCode }} {{ location.city }}</p>
      @if (location.operator) {
        <p class="operator">Betreiber: {{ location.operator }}</p>
      }
      <p class="availability">
        {{ location.evsesAvailable }} von {{ location.evsesTotal }} Ladepunkten frei
      </p>

      <h3>Ladepunkte</h3>
      @for (evse of detail.data.evses; track evse.id) {
        <article class="evse" [attr.data-evse]="evse.id">
          <h4>
            {{ evse.evseId }}
            @if (!evse.hasEvseId) {
              <small>(ID von chargeglobe vergeben)</small>
            }
          </h4>
          <p class="status">
            <span class="dot" [style.background]="statusOf(evse.status).color"></span
            >{{ statusOf(evse.status).label }}
            @if (evse.statusSince) {
              seit {{ evse.statusSince | date: 'dd.MM.yyyy HH:mm' }}
            }
            @if (evse.maxPowerKw !== null) {
              · bis {{ evse.maxPowerKw | number: '1.0-1' : 'en-US' }} kW
            }
          </p>
          <p class="connectors">{{ connectors(evse) }}</p>
          @switch (timelineState(evse.id)) {
            @case ('loading') {
              <p class="timeline-state">Verlauf wird geladen …</p>
            }
            @case ('error') {
              <p class="timeline-state error">Der Statusverlauf konnte nicht geladen werden.</p>
            }
            @default {
              <div
                class="timeline"
                role="img"
                [attr.aria-label]="'Statusverlauf der letzten ' + timelineDays + ' Tage'"
              >
                @for (segment of segments(evse.id); track segment.from.getTime()) {
                  <span
                    class="segment"
                    [style.left.%]="segment.left"
                    [style.width.%]="segment.width"
                    [style.background]="statusOf(segment.status).color"
                    [title]="segmentTitle(segment)"
                  ></span>
                }
              </div>
              <p class="timeline-axis">
                <span>{{ range().from | date: 'dd.MM. HH:mm' }}</span
                ><span>jetzt</span>
              </p>
              @if (segments(evse.id).length === 0) {
                <p class="timeline-state">
                  Keine Statusmeldungen in den letzten {{ timelineDays }} Tagen.
                </p>
              }
            }
          }
        </article>
      } @empty {
        <p id="no-evses">Für diesen Standort sind keine Ladepunkte bekannt.</p>
      }

      @if (detail.data.evses.length > 0) {
        <h3>Auslastung nach Wochentag und Stunde</h3>
        @if (detail.data.evses.length > 1) {
          <label id="heatmap-select">
            Ladepunkt
            <select (change)="heatmapOf.set($any($event.target).value)">
              <option value="all">Alle Ladepunkte</option>
              @for (evse of detail.data.evses; track evse.id) {
                <option [value]="evse.id">{{ evse.evseId }}</option>
              }
            </select>
          </label>
        }
        @switch (heatmapState()) {
          @case ('loading') {
            <p class="heatmap-state">Auslastung wird geladen …</p>
          }
          @case ('error') {
            <p class="heatmap-state error">Die Auslastung konnte nicht geladen werden.</p>
          }
          @default {
            @let heatmap = heatmapData();
            <p id="utilization">
              @if (heatmap.utilization !== null) {
                In den letzten {{ occupancyWeeks }} Wochen lud hier ein Fahrzeug bei
                {{ heatmap.utilization | percent: '1.0-0' : 'en-US' }} der
                {{ heatmap.observations }} Statusmeldungen.
              } @else {
                Keine Statusmeldungen in den letzten {{ occupancyWeeks }} Wochen.
              }
            </p>
            <table id="heatmap">
              <thead>
                <tr>
                  <th></th>
                  @for (hour of hours; track hour) {
                    <th scope="col">{{ hour % 3 === 0 ? hour : '' }}</th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (day of heatmap.days; track day.weekday) {
                  <tr>
                    <th scope="row">{{ dayLabels[day.weekday - 1] }}</th>
                    @for (share of day.chargingShare; track $index) {
                      <td
                        [style.background]="heatColor(share)"
                        [title]="cellTitle(day, $index)"
                      ></td>
                    }
                  </tr>
                }
              </tbody>
            </table>
            <p class="heatmap-legend">
              Anteil der Statusmeldungen „lädt“ je Stunde (Ortszeit Wien): hell = selten, dunkel =
              häufig, grau = keine Daten.
            </p>
          }
        }
      }

      @if (attribution().length > 0) {
        <p id="detail-attribution">{{ attribution().join(' · ') }}</p>
      }
    }
  `,
})
export class LocationDetailPage {
  private readonly service = inject(EvsesService);
  private readonly id = inject(ActivatedRoute).snapshot.paramMap.get('id')!;

  protected readonly timelineDays = TIMELINE_DAYS;
  protected readonly occupancyWeeks = OCCUPANCY_WEEKS;
  protected readonly dayLabels = DAY_LABELS;
  protected readonly hours = Array.from({ length: 24 }, (_, hour) => hour);
  protected readonly statusOf = statusOf;
  protected readonly heatColor = heatColor;

  protected readonly notice = signal<'missing' | 'failed' | null>(null);
  protected readonly detail = signal<LocationDetailResponse | null>(null);
  protected readonly range = signal({ from: new Date(), to: new Date() });
  private readonly timelines = signal<Record<string, Segment[] | 'error'>>({});
  private readonly occupancies = signal<Record<string, Occupancy | 'error'>>({});
  /** "all" or the id of the EVSE whose heatmap is shown. */
  protected readonly heatmapOf = signal('all');

  protected readonly attribution = computed(() => [
    ...new Set(this.detail()?.meta.attribution.map((source) => source.text) ?? []),
  ]);

  /** The loaded matrices of the selected EVSE(s). */
  private readonly selectedOccupancies = computed(() => {
    const selected = this.heatmapOf();
    return Object.entries(this.occupancies())
      .filter(([id]) => selected === 'all' || id === selected)
      .map(([, occupancy]) => occupancy);
  });

  protected readonly heatmapState = computed<LoadState>(() => {
    const evses = this.detail()!.data.evses;
    const wanted = this.heatmapOf() === 'all' ? evses.length : 1;
    const results = this.selectedOccupancies();
    if (results.some((result) => result !== 'error')) {
      return 'ready';
    }
    return results.length < wanted ? 'loading' : 'error';
  });

  /** Matrix and totals of the selection; several EVSEs are summed reading by reading. */
  protected readonly heatmapData = computed(() => {
    const loaded = this.selectedOccupancies().filter(
      (result): result is Occupancy => result !== 'error',
    );
    const observations = loaded.reduce((sum, occupancy) => sum + occupancy.observations, 0);
    const charging = loaded.reduce(
      (sum, occupancy) => sum + (occupancy.utilization ?? 0) * occupancy.observations,
      0,
    );
    return {
      days: combine(loaded.map((occupancy) => occupancy.days)),
      observations,
      utilization: observations > 0 ? charging / observations : null,
    };
  });

  constructor() {
    void this.load();
  }

  protected timelineState(evseId: string): LoadState {
    const segments = this.timelines()[evseId];
    return segments === undefined ? 'loading' : segments === 'error' ? 'error' : 'ready';
  }

  protected segments(evseId: string): Segment[] {
    return this.timelines()[evseId] as Segment[];
  }

  protected connectors(evse: EvseDetail): string {
    return evse.connectors
      .map((connector) =>
        [
          connector.standard,
          connector.format,
          connector.maxPowerKw === null ? null : `${connector.maxPowerKw} kW`,
        ]
          .filter((part) => part !== null)
          .join(' · '),
      )
      .join(', ');
  }

  protected segmentTitle(segment: Segment): string {
    const format = (date: Date) => formatDate(date, 'dd.MM. HH:mm', 'en-US');
    return `${statusOf(segment.status).label}: ${format(segment.from)} – ${format(segment.to)}`;
  }

  protected cellTitle(day: OccupancyDay, hour: number): string {
    const share = day.chargingShare[hour];
    const value = share === null ? 'keine Daten' : `${formatPercent(share, 'en-US', '1.0-0')} lädt`;
    return `${DAY_LABELS[day.weekday - 1]} ${hour}–${hour + 1} Uhr: ${value} (${day.observations[hour]} Meldungen)`;
  }

  private async load(): Promise<void> {
    let detail: LocationDetailResponse;
    try {
      detail = await this.service.location(this.id);
    } catch (error) {
      this.notice.set(
        error instanceof HttpErrorResponse && error.status === 404 ? 'missing' : 'failed',
      );
      return;
    }
    const to = new Date();
    const from = new Date(to.getTime() - TIMELINE_DAYS * 24 * 60 * 60 * 1000);
    this.range.set({ from, to });
    this.detail.set(detail);
    await Promise.all(
      detail.data.evses.flatMap((evse) => [
        this.loadTimeline(evse.id, from, to),
        this.loadOccupancy(evse.id),
      ]),
    );
  }

  private async loadTimeline(evseId: string, from: Date, to: Date): Promise<void> {
    try {
      const history = await this.service.history(evseId, from, to);
      this.timelines.update((all) => ({
        ...all,
        [evseId]: timeline(history.data.intervals, from, to),
      }));
    } catch {
      this.timelines.update((all) => ({ ...all, [evseId]: 'error' }));
    }
  }

  private async loadOccupancy(evseId: string): Promise<void> {
    try {
      const occupancy = await this.service.occupancy(evseId, OCCUPANCY_WEEKS);
      this.occupancies.update((all) => ({ ...all, [evseId]: occupancy.data }));
    } catch {
      this.occupancies.update((all) => ({ ...all, [evseId]: 'error' }));
    }
  }
}
