import { MARKER_KEYS, MarkerKey } from './availability';

/**
 * Properties a GeoJSON cluster sums up per marker (MapLibre clusterProperties): for every key the number of locations
 * in the cluster with that marker.
 */
export const CLUSTER_PROPERTIES = Object.fromEntries(
  MARKER_KEYS.map((key) => [key, ['+', ['case', ['==', ['get', 'marker'], key], 1, 0]]]),
) as Record<MarkerKey, unknown>;

/** The counts per marker of a cluster from its properties; missing ones count 0. */
export function clusterCounts(properties: Record<string, unknown>): Record<MarkerKey, number> {
  return Object.fromEntries(MARKER_KEYS.map((key) => [key, Number(properties[key] ?? 0)])) as Record<MarkerKey, number>;
}

/** Outer radius in px by the number of locations, as the clusters before. */
export function clusterRadius(total: number): number {
  return total < 50 ? 18 : total < 200 ? 22 : 28;
}

export interface PieColors {
  status: Record<MarkerKey, string>;
  /** Disc with the count in the middle. */
  center: string;
  onCenter: string;
  /** Thin outline against the map. */
  ring: string;
}

const point = (radius: number, size: number, turn: number): string => {
  const angle = turn * 2 * Math.PI - Math.PI / 2;
  return `${(size / 2 + radius * Math.cos(angle)).toFixed(2)} ${(size / 2 + radius * Math.sin(angle)).toFixed(2)}`;
};

/**
 * A cluster as donut chart (SVG): one segment per marker in the order of the legend, clockwise from the top, sized by
 * its share; in the middle a disc with the number of locations. The title lists the counts for screen readers and
 * tooltips.
 */
export function clusterPie(counts: Record<MarkerKey, number>, colors: PieColors, labels: Record<MarkerKey, string>): string {
  const total = MARKER_KEYS.reduce((sum, key) => sum + counts[key], 0);
  const outer = clusterRadius(total);
  const size = outer * 2 + 4;
  const inner = Math.round(outer * 0.62);
  const center = size / 2;
  const parts = MARKER_KEYS.filter((key) => counts[key] > 0);
  let start = 0;
  const segments = parts.map((key) => {
    const share = counts[key] / total;
    const end = start + share;
    const path =
      share >= 1
        ? `<circle cx="${center}" cy="${center}" r="${outer}" fill="${colors.status[key]}"/>`
        : `<path d="M${point(outer, size, start)} A${outer} ${outer} 0 ${share > 0.5 ? 1 : 0} 1 ${point(outer, size, end)} L${center} ${center}Z" fill="${colors.status[key]}"/>`;
    start = end;
    return path;
  });
  const title = parts.map((key) => `${counts[key]} ${labels[key]}`).join(', ');
  return (
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 ${size} ${size}" role="img">` +
    `<title>${total} Standorte: ${title}</title>` +
    `<circle cx="${center}" cy="${center}" r="${outer + 1.5}" fill="${colors.ring}"/>` +
    segments.join('') +
    `<circle cx="${center}" cy="${center}" r="${inner}" fill="${colors.center}"/>` +
    `<text x="${center}" y="${center}" text-anchor="middle" dominant-baseline="central" font-size="${outer < 20 ? 11 : 12}" font-weight="700" fill="${colors.onCenter}">${total}</text>` +
    `</svg>`
  );
}
