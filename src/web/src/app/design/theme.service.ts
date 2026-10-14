import { BreakpointObserver } from '@angular/cdk/layout';
import { DOCUMENT, Injectable, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { map } from 'rxjs';

/** The viewer's choice; "system" follows prefers-color-scheme. */
export type ThemeMode = 'system' | 'light' | 'dark';

export const THEME_STORAGE_KEY = 'chargeglobe.theme';

/**
 * Light and dark mode (docs/adr/0012). The theme in styles.scss is built with light-dark(), so the only switch is the
 * color-scheme of the document: empty follows the stylesheet ("light dark", i.e. the system), "light" or "dark" pins
 * it. The choice stays in the browser; storage that is blocked or unavailable only means the choice is not kept.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly media = this.document.defaultView!.matchMedia('(prefers-color-scheme: dark)');
  private readonly systemDark = signal(this.media.matches);

  readonly mode = signal<ThemeMode>(readStoredMode());
  /** Whether dark colours are in effect now, also when following the system. */
  readonly dark = computed(() => this.mode() === 'dark' || (this.mode() === 'system' && this.systemDark()));

  constructor() {
    this.media.addEventListener('change', (event) => this.systemDark.set(event.matches));
    this.apply(this.mode());
  }

  set(mode: ThemeMode): void {
    this.mode.set(mode);
    this.apply(mode);
    try {
      localStorage.setItem(THEME_STORAGE_KEY, mode);
    } catch {
      // Storage blocked (private mode, policy): the choice holds until the page is closed.
    }
  }

  private apply(mode: ThemeMode): void {
    this.document.documentElement.style.colorScheme = mode === 'system' ? '' : mode;
  }
}

function readStoredMode(): ThemeMode {
  try {
    const stored = localStorage.getItem(THEME_STORAGE_KEY);
    return stored === 'light' || stored === 'dark' ? stored : 'system';
  } catch {
    return 'system';
  }
}

/** Below the Material "medium" window class (840 px): navigation in a side sheet, vertical stepper, bottom sheet. */
export const MOBILE_QUERY = '(max-width: 839.98px)';

@Injectable({ providedIn: 'root' })
export class Viewport {
  readonly mobile = toSignal(
    inject(BreakpointObserver)
      .observe(MOBILE_QUERY)
      .pipe(map((state) => state.matches)),
    { initialValue: false },
  );
}
