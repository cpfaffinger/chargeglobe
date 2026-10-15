import { Injectable, InjectionToken, signal } from '@angular/core';

export const LEGEND_STORAGE_KEY = 'chargeglobe.map.legend';
export const TRUNCATED_SNOOZE_KEY = 'chargeglobe.map.truncated-snooze-until';

/** Once closed, the notice about a truncated map stays away for a day. */
export const TRUNCATED_SNOOZE_MS = 24 * 60 * 60 * 1000;

/** How long the notice about a truncated map shows before it hides itself; specs shorten it. */
export const TRUNCATED_NOTICE_MS = new InjectionToken<number>('TRUNCATED_NOTICE_MS', { factory: () => 6000 });

/**
 * What the viewer chose to see on the map, kept in the browser (localStorage): the colour legend shown or folded, and
 * until when the notice about a truncated map is snoozed. Without storage the choices hold until the page is closed.
 */
@Injectable({ providedIn: 'root' })
export class MapHints {
  readonly legendVisible = signal(readLegend());

  setLegend(visible: boolean): void {
    this.legendVisible.set(visible);
    try {
      localStorage.setItem(LEGEND_STORAGE_KEY, visible ? '1' : '0');
    } catch {
      // Storage blocked: the choice holds until the page is closed.
    }
  }

  truncatedSnoozed(now = Date.now()): boolean {
    try {
      return Number(localStorage.getItem(TRUNCATED_SNOOZE_KEY) ?? 0) > now;
    } catch {
      return false;
    }
  }

  snoozeTruncated(now = Date.now()): void {
    try {
      localStorage.setItem(TRUNCATED_SNOOZE_KEY, String(now + TRUNCATED_SNOOZE_MS));
    } catch {
      // Storage blocked: the notice comes back with the next truncated load.
    }
  }
}

function readLegend(): boolean {
  try {
    return localStorage.getItem(LEGEND_STORAGE_KEY) !== '0';
  } catch {
    return true;
  }
}
