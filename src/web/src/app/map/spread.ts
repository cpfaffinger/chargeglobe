/**
 * Locations at (nearly) the same spot, e.g. two charging stations a source lists separately with the same
 * coordinates: their markers are drawn side by side around the spot instead of on top of each other, so each stays
 * visible and can be tapped. The map engine picks the offset per marker from {@link SPREAD_OFFSETS} by its slot; a spot
 * with more than {@link MAX_SPREAD} locations becomes one park marker.
 */

/** Locations closer than this share a spot. */
export const SAME_SPOT_METERS = 5;

/** Most markers on the ring around a spot; a spot with more gets one park marker instead. */
export const MAX_SPREAD = 8;

const METERS_PER_DEGREE = 111_200;

/** Offset in px of slot `n-i` (marker i of n at one spot): around the spot, the first one to the left. */
export const SPREAD_OFFSETS: Record<string, [number, number]> = Object.fromEntries(
  Array.from({ length: MAX_SPREAD - 1 }, (_, index) => index + 2).flatMap((count) => {
    const radius = count <= 2 ? 15 : count <= 4 ? 18 : 22;
    return Array.from({ length: count }, (_, slot) => {
      const angle = Math.PI + (slot * 2 * Math.PI) / count;
      return [`${count}-${slot}`, [round(radius * Math.cos(angle)), round(radius * Math.sin(angle))]];
    });
  }),
);

function round(value: number): number {
  return Math.round(value * 10) / 10 || 0;
}

interface Placed {
  id: string;
  latitude: number;
  longitude: number;
}

/** Groups the locations by spot: within {@link SAME_SPOT_METERS} of the first location of a spot. */
export function spots<T extends Placed>(locations: readonly T[]): T[][] {
  const cells = new Map<string, T[][]>();
  const groups: T[][] = [];
  const cellOf = (location: Placed) => {
    const latitudeStep = SAME_SPOT_METERS / METERS_PER_DEGREE;
    const longitudeStep = latitudeStep / Math.cos((location.latitude * Math.PI) / 180);
    return [Math.floor(location.latitude / latitudeStep), Math.floor(location.longitude / longitudeStep)];
  };
  const near = (a: Placed, b: Placed) => {
    const north = (a.latitude - b.latitude) * METERS_PER_DEGREE;
    const east = (a.longitude - b.longitude) * METERS_PER_DEGREE * Math.cos((a.latitude * Math.PI) / 180);
    return north * north + east * east < SAME_SPOT_METERS * SAME_SPOT_METERS;
  };
  for (const location of locations) {
    const [row, column] = cellOf(location);
    let spot: T[] | undefined;
    for (let dRow = -1; dRow <= 1 && !spot; dRow++) {
      for (let dColumn = -1; dColumn <= 1 && !spot; dColumn++) {
        spot = cells.get(`${row + dRow}:${column + dColumn}`)?.find((candidate) => near(candidate[0], location));
      }
    }
    if (!spot) {
      spot = [];
      groups.push(spot);
      const key = `${row}:${column}`;
      cells.set(key, [...(cells.get(key) ?? []), spot]);
    }
    spot.push(location);
  }
  return groups.map((group) => [...group].sort((a, b) => a.id.localeCompare(b.id)));
}

/**
 * The slot of every location that shares its spot with up to {@link MAX_SPREAD} - 1 others (key of
 * {@link SPREAD_OFFSETS}); locations alone and those of crowded spots (see {@link crowdOf}) have none. Members of a
 * spot are ordered by id, so a marker keeps its place when the map reloads.
 */
export function spreadSlots(locations: readonly Placed[]): Map<string, string> {
  const slots = new Map<string, string>();
  for (const spot of spots(locations).filter((members) => members.length > 1 && members.length <= MAX_SPREAD)) {
    spot.forEach((location, index) => slots.set(location.id, `${spot.length}-${index}`));
  }
  return slots;
}

/**
 * Spots with more locations than the ring holds (e.g. a garage whose wallboxes the source lists separately with the
 * same coordinates): location id → id of the spot, its first location by id. The map draws one park marker for such a
 * spot instead of its points.
 */
export function crowdOf(locations: readonly Placed[]): Map<string, string> {
  const crowd = new Map<string, string>();
  for (const spot of spots(locations).filter((members) => members.length > MAX_SPREAD)) {
    spot.forEach((location) => crowd.set(location.id, spot[0].id));
  }
  return crowd;
}

/**
 * MapLibre expression for a px offset by the slot of the feature (property `spread`), added to `base`; `perUnit` px
 * make one unit of the property (1 for icon-offset in px, the text size for text-offset in ems).
 */
export function offsetExpression(base: [number, number], perUnit: number): unknown[] {
  return [
    'match',
    ['get', 'spread'],
    ...Object.entries(SPREAD_OFFSETS).flatMap(([slot, [x, y]]) => [
      slot,
      ['literal', [round(base[0] + x / perUnit), round(base[1] + y / perUnit)]],
    ]),
    ['literal', base],
  ];
}
