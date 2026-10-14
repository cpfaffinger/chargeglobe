import { AVAILABILITY } from '../map/availability';
import { OccupancyDay, StatusInterval } from './evses.service';

/** Colour and German label per OCPI status; the colours follow the map legend. */
export const EVSE_STATUS: Record<string, { color: string; label: string }> = {
  AVAILABLE: { color: AVAILABILITY.AVAILABLE.color, label: 'frei' },
  CHARGING: { color: AVAILABILITY.OCCUPIED.color, label: 'lädt' },
  BLOCKED: { color: '#ad5700', label: 'blockiert' },
  RESERVED: { color: '#f9a825', label: 'reserviert' },
  OUTOFORDER: { color: AVAILABILITY.OUT_OF_ORDER.color, label: 'außer Betrieb' },
  INOPERATIVE: { color: '#8e0000', label: 'nicht betriebsbereit' },
  PLANNED: { color: '#b0bec5', label: 'geplant' },
  REMOVED: { color: '#546e7a', label: 'entfernt' },
  UNKNOWN: { color: AVAILABILITY.UNKNOWN.color, label: 'unbekannt' },
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

/** Cell colour of the heatmap: grey without readings, otherwise from light to dark orange. */
export function heatColor(share: number | null): string {
  return share === null ? '#eceff1' : `hsl(28 90% ${Math.round(95 - share * 55)}%)`;
}
