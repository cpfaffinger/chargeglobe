import { Availability } from './locations.service';

/** Marker colour and legend text per availability; shared by the map engine and the legend. */
export const AVAILABILITY: Record<Availability, { color: string; label: string }> = {
  AVAILABLE: { color: '#2e7d32', label: 'frei' },
  OCCUPIED: { color: '#ef6c00', label: 'belegt' },
  OUT_OF_ORDER: { color: '#c62828', label: 'außer Betrieb' },
  UNKNOWN: { color: '#78909c', label: 'Status unbekannt' },
};

export const AVAILABILITIES = Object.keys(AVAILABILITY) as Availability[];
