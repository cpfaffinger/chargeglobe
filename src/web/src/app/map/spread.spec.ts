import { MAX_SPREAD, SPREAD_OFFSETS, offsetExpression, spreadSlots } from './spread';

/** A location the given metres north and east of a spot in Lower Austria. */
const at = (id: string, north: number, east = 0) => ({
  id,
  latitude: 48.2 + north / 111_200,
  longitude: 15.6 + east / (111_200 * Math.cos((48.2 * Math.PI) / 180)),
});

describe('spread of markers at one spot', () => {
  it('puts locations at the same spot side by side, ordered by id, and leaves the others alone', () => {
    const slots = spreadSlots([at('b', 0), at('a', 0), at('far', 50), at('c', 3, 1)]);

    expect([...slots.entries()].sort()).toEqual([
      ['a', '3-0'],
      ['b', '3-1'],
      ['c', '3-2'],
    ]);
    expect(slots.has('far')).toBeFalse();
    expect(spreadSlots([at('x', 0), at('y', 6)]).size).toBe(0);
  });

  it('joins neighbours across the cells of the grid', () => {
    // Close to each other, but on both sides of a cell border.
    const step = 5 / 111_200;
    const border = Math.ceil(48.2 / step) * step;
    const slots = spreadSlots([
      { id: 'south', latitude: border - step / 10, longitude: 15.6 },
      { id: 'north', latitude: border + step / 10, longitude: 15.6 },
    ]);

    expect([...slots.values()].sort()).toEqual(['2-0', '2-1']);
  });

  it('puts more markers than the ring holds on the ring again', () => {
    const many = Array.from({ length: MAX_SPREAD + 2 }, (_, index) => at(`s${index}`, 0));

    const slots = spreadSlots(many);

    expect(slots.get('s0')).toBe(`${MAX_SPREAD}-0`);
    expect(slots.get('s8')).toBe(`${MAX_SPREAD}-0`);
    expect(slots.get('s9')).toBe(`${MAX_SPREAD}-1`);
  });

  it('offsets the markers around the spot, the first one to the left', () => {
    expect(SPREAD_OFFSETS['2-0']).toEqual([-15, 0]);
    expect(SPREAD_OFFSETS['2-1']).toEqual([15, 0]);
    expect(SPREAD_OFFSETS['4-1']).toEqual([0, -18]);
    expect(Object.keys(SPREAD_OFFSETS).length).toBe(2 + 3 + 4 + 5 + 6 + 7 + 8);
  });

  it('builds the offset expressions for icons in px and texts in ems', () => {
    const icon = offsetExpression([0, 0], 1);
    const label = offsetExpression([1.1, -0.9], 11);

    expect(icon.slice(0, 2)).toEqual(['match', ['get', 'spread']]);
    expect(icon[icon.indexOf('2-0') + 1]).toEqual(['literal', [-15, 0]]);
    expect(icon[icon.length - 1]).toEqual(['literal', [0, 0]]);
    expect(label[label.indexOf('2-1') + 1]).toEqual(['literal', [2.5, -0.9]]);
    expect(label[label.length - 1]).toEqual(['literal', [1.1, -0.9]]);
  });
});
