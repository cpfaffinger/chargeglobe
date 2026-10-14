import { AVAILABILITY, tokenColor } from '../map/availability';
import { IconName } from '../design/icon';
import { OccupancyDay, StatusInterval } from './evses.service';

const status = (availability: keyof typeof AVAILABILITY, label: string) => ({
  color: tokenColor(AVAILABILITY[availability].token),
  icon: AVAILABILITY[availability].icon,
  label,
});

/** Colour (token of the map legend), symbol and German label per OCPI status. */
export const EVSE_STATUS: Record<string, { color: string; icon: IconName; label: string }> = {
  AVAILABLE: status('AVAILABLE', 'frei'),
  CHARGING: status('OCCUPIED', 'lädt'),
  BLOCKED: status('OCCUPIED', 'blockiert'),
  RESERVED: status('OCCUPIED', 'reserviert'),
  OUTOFORDER: status('OUT_OF_ORDER', 'außer Betrieb'),
  INOPERATIVE: status('OUT_OF_ORDER', 'nicht betriebsbereit'),
  PLANNED: status('UNKNOWN', 'geplant'),
  REMOVED: status('UNKNOWN', 'entfernt'),
  UNKNOWN: status('UNKNOWN', 'unbekannt'),
};

export const statusOf = (status: string) => EVSE_STATUS[status] ?? EVSE_STATUS['UNKNOWN'];

/** Part of a timeline bar: position and width in percent of the shown range. */
export interface Segment {
  status: string;
  from: Date;
  to: Date;
  left: number;
  width: number;
}

/** Clips the intervals to [from, to] and places them on a bar; an open interval lasts until `to`. */
export function timeline(intervals: StatusInterval[], from: Date, to: Date): Segment[] {
  const start = from.getTime();
  const span = to.getTime() - start;
  return intervals
    .map((interval) => {
      const begin = Math.max(start, Date.parse(interval.from));
      const end = Math.min(
        to.getTime(),
        interval.to === null ? to.getTime() : Date.parse(interval.to),
      );
      return { interval, begin, end };
    })
    .filter(({ begin, end }) => end > begin)
    .map(({ interval, begin, end }) => ({
      status: interval.status,
      from: new Date(begin),
      to: new Date(end),
      left: ((begin - start) / span) * 100,
      width: ((end - begin) / span) * 100,
    }));
}

/** Sums the occupancy of several EVSEs: charging readings and readings per weekday and hour. */
export function combine(matrices: OccupancyDay[][]): OccupancyDay[] {
  return matrices[0].map((row, index) => {
    const observations = row.observations.map((_, hour) =>
      matrices.reduce((sum, days) => sum + days[index].observations[hour], 0),
    );
    const charging = row.observations.map((_, hour) =>
      matrices.reduce(
        (sum, days) =>
          sum + (days[index].chargingShare[hour] ?? 0) * days[index].observations[hour],
        0,
      ),
    );
    return {
      weekday: row.weekday,
      day: row.day,
      observations,
      chargingShare: observations.map((count, hour) => (count > 0 ? charging[hour] / count : null)),
    };
  });
}

/**
 * Cell colour of the heatmap: the primary colour of the theme with 8 % (never) to 100 % (always) opacity, so it
 * follows light and dark mode; without readings the highest surface container colour.
 */
export function heatColor(share: number | null): string {
  return share === null
    ? 'var(--mat-sys-surface-container-highest)'
    : `color-mix(in srgb, var(--mat-sys-primary) ${Math.round(8 + share * 92)}%, transparent)`;
}
