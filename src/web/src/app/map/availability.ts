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
