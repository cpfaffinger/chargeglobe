import { DatePipe, formatDate, formatPercent } from '@angular/common';
import { Component, input } from '@angular/core';
import { OccupancyDay } from './evses.service';
import { Segment, heatColor, statusOf } from './status';

export const DAY_LABELS = ['Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa', 'So'];

/** Status timeline: one bar over the range, a segment per status period in the colour of the map legend. */
@Component({
  imports: [DatePipe],
  selector: 'app-status-timeline',
  template: `
    <div class="timeline" role="img" [attr.aria-label]="label()">
      @for (segment of segments(); track segment.from.getTime()) {
        <span
          class="segment"
          [style.left.%]="segment.left"
          [style.width.%]="segment.width"
          [style.background]="statusOf(segment.status).color"
          [title]="title(segment)"
        ></span>
      }
    </div>
    <p class="timeline-axis">
      <span>{{ from() | date: 'dd.MM. HH:mm' }}</span><span>jetzt</span>
    </p>
  `,
})
export class StatusTimeline {
  readonly segments = input.required<Segment[]>();
  readonly from = input.required<Date>();
  readonly label = input.required<string>();

  protected readonly statusOf = statusOf;

  protected title(segment: Segment): string {
    const format = (date: Date) => formatDate(date, 'dd.MM. HH:mm', 'en-US');
    return `${statusOf(segment.status).label}: ${format(segment.from)} – ${format(segment.to)}`;
  }
}

/** Heatmap weekday × hour of the share of readings with status CHARGING (local time Vienna). */
@Component({
  selector: 'app-occupancy-heatmap',
  template: `
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
        @for (day of days(); track day.weekday) {
          <tr>
            <th scope="row">{{ dayLabels[day.weekday - 1] }}</th>
            @for (share of day.chargingShare; track $index) {
              <td [style.background]="heatColor(share)" [title]="title(day, $index)"></td>
            }
          </tr>
        }
      </tbody>
    </table>
    <p class="heatmap-legend">
      Anteil der Statusmeldungen „lädt“ je Stunde (Ortszeit Wien): hell = selten, kräftig = häufig, grau = keine Daten.
    </p>
  `,
})
export class OccupancyHeatmap {
  readonly days = input.required<OccupancyDay[]>();

  protected readonly hours = Array.from({ length: 24 }, (_, hour) => hour);
  protected readonly dayLabels = DAY_LABELS;
  protected readonly heatColor = heatColor;

  protected title(day: OccupancyDay, hour: number): string {
    const share = day.chargingShare[hour];
    const value = share === null ? 'keine Daten' : `${formatPercent(share, 'en-US', '1.0-0')} lädt`;
    return `${DAY_LABELS[day.weekday - 1]} ${hour}–${hour + 1} Uhr: ${value} (${day.observations[hour]} Meldungen)`;
  }
}
