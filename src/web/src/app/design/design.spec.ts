import { BreakpointObserver } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AVAILABILITY, resolveColors, tokenColor } from '../map/availability';
import { AppIcon, ICONS } from './icon';
import { GermanPaginatorIntl } from './paginator-intl';
import { COMPACT_NAV_QUERY, MOBILE_QUERY, THEME_STORAGE_KEY, ThemeService, Viewport } from './theme.service';

describe('ThemeService', () => {
  let systemDark: (matches: boolean) => void;

  /** Lets matchMedia answer like a system in light mode and remembers the change listener. */
  function fakeSystem(): void {
    spyOn(window, 'matchMedia').and.returnValue({
      matches: false,
      addEventListener: (_type: string, listener: (event: { matches: boolean }) => void) =>
        (systemDark = (matches) => listener({ matches })),
    } as unknown as MediaQueryList);
  }

  beforeEach(() => fakeSystem());

  afterEach(() => {
    localStorage.removeItem(THEME_STORAGE_KEY);
    document.documentElement.style.colorScheme = '';
  });

  it('follows the system by default and reacts to its changes', () => {
    const theme = TestBed.inject(ThemeService);

    expect(theme.mode()).toBe('system');
    expect(theme.dark()).toBeFalse();
    expect(document.documentElement.style.colorScheme).toBe('');
    expect(window.matchMedia).toHaveBeenCalledWith('(prefers-color-scheme: dark)');

    systemDark(true);
    expect(theme.dark()).toBeTrue();
  });

  it('pins light or dark mode and keeps the choice in the browser', () => {
    const theme = TestBed.inject(ThemeService);

    theme.set('dark');
    expect(theme.dark()).toBeTrue();
    expect(document.documentElement.style.colorScheme).toBe('dark');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');

    theme.set('light');
    systemDark(true);
    expect(theme.dark()).toBeFalse();
    expect(document.documentElement.style.colorScheme).toBe('light');
  });

  it('starts with a stored choice and ignores unknown values', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'dark');
    expect(TestBed.inject(ThemeService).mode()).toBe('dark');
    expect(document.documentElement.style.colorScheme).toBe('dark');

    TestBed.resetTestingModule();
    localStorage.setItem(THEME_STORAGE_KEY, 'sepia');
    expect(TestBed.inject(ThemeService).mode()).toBe('system');
  });

  it('works without browser storage', () => {
    spyOn(Storage.prototype, 'getItem').and.throwError('blocked');
    spyOn(Storage.prototype, 'setItem').and.throwError('blocked');
    const theme = TestBed.inject(ThemeService);

    expect(theme.mode()).toBe('system');
    theme.set('dark');
    expect(theme.mode()).toBe('dark');
    expect(document.documentElement.style.colorScheme).toBe('dark');
  });
});

describe('Viewport', () => {
  it('reports small screens from the breakpoint observer', () => {
    const observer = jasmine.createSpyObj<BreakpointObserver>('BreakpointObserver', ['observe']);
    observer.observe.and.callFake((query) => of({ matches: query === COMPACT_NAV_QUERY, breakpoints: {} }));
    TestBed.configureTestingModule({ providers: [{ provide: BreakpointObserver, useValue: observer }] });

    // A tablet: menu button, but no phone layout.
    const viewport = TestBed.inject(Viewport);
    expect([viewport.mobile(), viewport.compactNav()]).toEqual([false, true]);
    expect(observer.observe.calls.allArgs()).toEqual([[MOBILE_QUERY], [COMPACT_NAV_QUERY]]);
  });
});

describe('AppIcon', () => {
  it('draws the named symbol and hides it from screen readers', async () => {
    const fixture = TestBed.createComponent(AppIcon);
    fixture.componentRef.setInput('name', 'bolt');
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.getAttribute('aria-hidden')).toBe('true');
    expect(host.querySelector('path')!.getAttribute('d')).toBe(ICONS.bolt);
  });
});

describe('status colours', () => {
  afterEach(() => (document.documentElement.style.colorScheme = ''));

  it('resolves the tokens of the current mode for the map renderer', () => {
    const tokens = [AVAILABILITY.AVAILABLE.token, AVAILABILITY.OUT_OF_ORDER.token, '--cg-on-status'];

    document.documentElement.style.colorScheme = 'light';
    expect(resolveColors(tokens)).toEqual({
      '--cg-status-available': 'rgb(27, 127, 58)',
      '--cg-status-out-of-order': 'rgb(179, 38, 30)',
      '--cg-on-status': 'rgb(255, 255, 255)',
    });

    document.documentElement.style.colorScheme = 'dark';
    expect(resolveColors(tokens)).toEqual({
      '--cg-status-available': 'rgb(109, 213, 140)',
      '--cg-status-out-of-order': 'rgb(255, 180, 171)',
      '--cg-on-status': 'rgb(15, 20, 22)',
    });
    expect(document.body.querySelector(':scope > span:last-child[style*="--cg"]')).toBeNull();
  });

  it('writes a token as CSS value', () => {
    expect(tokenColor('--cg-status-unknown')).toBe('var(--cg-status-unknown)');
  });
});

describe('GermanPaginatorIntl', () => {
  it('names the page, the page count and the entries', () => {
    const intl = new GermanPaginatorIntl();

    expect(intl.getRangeLabel(1, 25, 60)).toBe('Seite 2 von 3 (60 Einträge)');
    expect(intl.getRangeLabel(0, 25, 0)).toBe('Seite 1 von 1 (0 Einträge)');
    expect(intl.nextPageLabel).toBe('Nächste Seite');
  });
});
