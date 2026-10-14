import { IconName } from '../design/icon';
import { Availability } from './locations.service';

/**
 * Colour token (styles.scss, light-dark() per mode), symbol and legend text per availability; shared by the map
 * engine, the legend and the info card. The symbol keeps a status recognisable without its colour.
 */
export const AVAILABILITY: Record<Availability, { token: string; icon: IconName; label: string }> = {
  AVAILABLE: { token: '--cg-status-available', icon: 'check', label: 'frei' },
  OCCUPIED: { token: '--cg-status-occupied', icon: 'bolt', label: 'belegt' },
  OUT_OF_ORDER: { token: '--cg-status-out-of-order', icon: 'close', label: 'außer Betrieb' },
  UNKNOWN: { token: '--cg-status-unknown', icon: 'help', label: 'Status unbekannt' },
};

export const AVAILABILITIES = Object.keys(AVAILABILITY) as Availability[];

/** Marker of a location on the map: free locations by the power that is free now, the others by availability. */
export type MarkerKey = 'AVAILABLE' | 'AVAILABLE_FAST' | 'AVAILABLE_HPC' | Exclude<Availability, 'AVAILABLE'>;

/** Free power from which a location counts as fast (above) or high-power charging (from), in kW. */
export const FAST_ABOVE_KW = 22;
export const HPC_FROM_KW = 150;

/** Colour token, symbol and legend text per marker, in the order of the legend. */
export const MARKERS: Record<MarkerKey, { token: string; icon: IconName; label: string }> = {
  AVAILABLE: { ...AVAILABILITY.AVAILABLE, label: `frei bis ${FAST_ABOVE_KW} kW` },
  AVAILABLE_FAST: { token: '--cg-power-fast', icon: 'check', label: `frei ${FAST_ABOVE_KW + 1}–${HPC_FROM_KW - 1} kW` },
  AVAILABLE_HPC: { token: '--cg-power-hpc', icon: 'check', label: `frei ab ${HPC_FROM_KW} kW` },
  OCCUPIED: AVAILABILITY.OCCUPIED,
  OUT_OF_ORDER: AVAILABILITY.OUT_OF_ORDER,
  UNKNOWN: AVAILABILITY.UNKNOWN,
};

export const MARKER_KEYS = Object.keys(MARKERS) as MarkerKey[];

/** The marker of a location; a free location without known power counts as up to 22 kW. */
export function markerOf(location: { availability: Availability; maxAvailablePowerKw: number | null }): MarkerKey {
  if (location.availability !== 'AVAILABLE') {
    return location.availability;
  }
  const power = location.maxAvailablePowerKw ?? 0;
  return power >= HPC_FROM_KW ? 'AVAILABLE_HPC' : power > FAST_ABOVE_KW ? 'AVAILABLE_FAST' : 'AVAILABLE';
}

/** CSS value of a token, for templates. */
export const tokenColor = (token: string): string => `var(${token})`;

/**
 * The colours of tokens as the browser resolves them in the current colour scheme. Needed by renderers that do not
 * read CSS (the WebGL map): custom properties come back unresolved (light-dark(…)), so each is applied to a probe.
 */
export function resolveColors(tokens: string[], host: HTMLElement = document.body): Record<string, string> {
  const probe = host.ownerDocument.createElement('span');
  host.appendChild(probe);
  const colors = Object.fromEntries(
    tokens.map((token) => {
      probe.style.color = `var(${token})`;
      return [token, getComputedStyle(probe).color];
    }),
  );
  probe.remove();
  return colors;
}
