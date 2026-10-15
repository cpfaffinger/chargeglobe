import { DOCUMENT, Injectable, inject } from '@angular/core';
import { MapView } from './map-view.store';

/** Result of "Teilen": the system share sheet was used, the link was copied, or neither worked. */
export type ShareResult = 'shared' | 'copied' | 'cancelled' | 'failed';

const HASH = /^#map=(\d+(?:\.\d+)?)\/(-?\d+(?:\.\d+)?)\/(-?\d+(?:\.\d+)?)$/;

/**
 * The current view in the address of the map page, as fragment `#map=<zoom>/<latitude>/<longitude>` (the format of
 * openstreetmap.org), so the address shows the current view and can be shared. The fragment is replaced in place
 * (history.replaceState): moving the map adds no history entries and causes no navigation.
 */
@Injectable({ providedIn: 'root' })
export class MapLink {
  private readonly document = inject(DOCUMENT);

  /** The view of the address; null without a valid fragment. */
  read(): MapView | null {
    const match = HASH.exec(this.document.location.hash);
    if (!match) {
      return null;
    }
    const [zoom, latitude, longitude] = match.slice(1).map(Number);
    return zoom <= 24 && Math.abs(latitude) <= 90 && Math.abs(longitude) <= 180 ? { latitude, longitude, zoom } : null;
  }

  write(view: MapView): void {
    const { pathname, search } = this.document.location;
    const hash = `#map=${view.zoom.toFixed(2)}/${view.latitude.toFixed(5)}/${view.longitude.toFixed(5)}`;
    this.document.defaultView!.history.replaceState(this.document.defaultView!.history.state, '', `${pathname}${search}${hash}`);
  }

  /** The fragment without '#', for navigations that should keep it. */
  fragment(): string | undefined {
    return this.document.location.hash.slice(1) || undefined;
  }

  /**
   * Shares the address of the current page (on the map: the view with filters) under the title: system share sheet if
   * there is one, else the clipboard.
   */
  async share(title = 'chargeglobe'): Promise<ShareResult> {
    const navigator = this.document.defaultView!.navigator;
    const url = this.document.location.href;
    if (typeof navigator.share === 'function') {
      try {
        await navigator.share({ title, url });
        return 'shared';
      } catch (error) {
        if (error instanceof DOMException && error.name === 'AbortError') {
          return 'cancelled';
        }
      }
    }
    try {
      await navigator.clipboard.writeText(url);
      return 'copied';
    } catch {
      return 'failed';
    }
  }
}
