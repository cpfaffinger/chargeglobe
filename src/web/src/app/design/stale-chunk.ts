import { DOCUMENT, ErrorHandler, InjectionToken, inject } from '@angular/core';

/** Reloads the page; replaceable in tests. */
export const RELOAD = new InjectionToken<() => void>('RELOAD', {
  providedIn: 'root',
  factory: () => {
    const document = inject(DOCUMENT);
    return () => document.defaultView?.location.reload();
  },
});

/** Key in sessionStorage with the time of the last reload, so that a broken release does not reload endlessly. */
export const RELOADED_AT = 'chargeglobe.reloaded-at';

/** A second reload within this time is pointless: the fresh release was already loaded. */
export const RELOAD_COOLDOWN_MS = 60_000;

/**
 * What browsers say when a lazy chunk of an old release is gone (Chrome, Firefox, Safari, module scripts), and NG0750,
 * with which Angular reports a deferred block whose dependencies failed to load (in production without the reason).
 */
const STALE_CHUNK = /dynamically imported module|Importing a module script failed|Failed to load module script|Loading chunk|NG0750/i;

/** True if the error (or what it wraps) is a failed load of a lazy chunk. */
export function isStaleChunkError(error: unknown): boolean {
  for (let current = error, depth = 0; current && depth < 5; depth++) {
    const message = typeof current === 'string' ? current : (current as { message?: unknown }).message;
    if (typeof message === 'string' && STALE_CHUNK.test(message)) {
      return true;
    }

    // Unhandled promise rejections arrive wrapped (rejection), errors with a cause nested.
    const wrapped = current as { rejection?: unknown; cause?: unknown };
    current = wrapped.rejection ?? wrapped.cause;
  }

  return false;
}

/**
 * Every deploy replaces the hashed bundles; a page that was opened before then cannot load the lazy pages
 * (account, administration) and deferred panels (filters, map options, location details) any more: their chunks are
 * gone. Such an error reloads the page once, which fetches the current index.html with the current chunks; any
 * other error goes to the console as usual.
 */
export class ReloadOnStaleChunk extends ErrorHandler {
  private readonly reload = inject(RELOAD);
  private readonly window = inject(DOCUMENT).defaultView;

  override handleError(error: unknown): void {
    if (isStaleChunkError(error) && this.markReload()) {
      this.reload();
      return;
    }

    super.handleError(error);
  }

  /** Remembers the reload; false if the page was reloaded for this reason a moment ago (the storage may be unavailable). */
  private markReload(): boolean {
    try {
      const storage = this.window?.sessionStorage;
      const last = Number(storage?.getItem(RELOADED_AT) ?? 0);
      if (Date.now() - last < RELOAD_COOLDOWN_MS) {
        return false;
      }

      storage?.setItem(RELOADED_AT, String(Date.now()));
    } catch {
      // Private mode or blocked storage: reload anyway, the cooldown just does not apply.
    }

    return true;
  }
}
