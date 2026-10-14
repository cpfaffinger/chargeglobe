import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { FAVORITES_REFRESH_MS, FavoritesPage } from '../pages/favorites.page';
import { ME, fakeAuth, flush, locationSummary, text } from '../testing';
import { FavoriteButton } from './favorite-button';
import { FavoritesResponse, FavoritesService } from './favorites.service';

const response = (...ids: string[]): FavoritesResponse => ({
  data: ids.map((id, index) => ({
    location: locationSummary({ id, name: index === 0 ? 'Parkgarage Stephansplatz' : null, maxAvailablePowerKw: index === 0 ? 150 : null }),
    addedAt: '2026-10-14T08:00:00Z',
  })),
  meta: { returned: ids.length, max: 200, attribution: [{ source: 'econtrol', text: 'Datenquelle: E-Control', license: 'Terms' }] },
});

describe('FavoritesService', () => {
  let http: HttpTestingController;
  let auth: ReturnType<typeof fakeAuth>;

  beforeEach(() => {
    auth = fakeAuth();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), { provide: AuthService, useValue: auth }] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('loads the favorites after the login, keeps changes and forgets them after the logout', async () => {
    const favorites = TestBed.inject(FavoritesService);
    TestBed.tick();
    http.expectNone('/api/v1/me/favorites');

    auth.me.set(ME);
    TestBed.tick();
    http.expectOne('/api/v1/me/favorites').flush(response('loc-1'));
    await flush();
    expect(favorites.isFavorite('loc-1')).toBeTrue();

    const added = favorites.add('loc-2');
    const put = http.expectOne('/api/v1/me/favorites/loc-2');
    expect(put.request.method).toBe('PUT');
    put.flush(null, { status: 204, statusText: 'No Content' });
    await added;
    expect(favorites.isFavorite('loc-2')).toBeTrue();

    const removed = favorites.remove('loc-1');
    const del = http.expectOne('/api/v1/me/favorites/loc-1');
    expect(del.request.method).toBe('DELETE');
    del.flush(null, { status: 204, statusText: 'No Content' });
    await removed;
    expect(favorites.isFavorite('loc-1')).toBeFalse();

    // Calls at the same time share one request.
    const first = favorites.list();
    const second = favorites.list();
    http.expectOne('/api/v1/me/favorites').flush(response('loc-3'));
    expect(await first).toBe(await second);

    auth.me.set(null);
    TestBed.tick();
    expect(favorites.isFavorite('loc-3')).toBeFalse();
  });

  it('ignores a failed load after the login', async () => {
    auth.me.set(ME);
    const favorites = TestBed.inject(FavoritesService);
    TestBed.tick();
    http.expectOne('/api/v1/me/favorites').flush({ title: 'Error' }, { status: 500, statusText: 'Error' });
    await flush();

    expect(favorites.isFavorite('loc-1')).toBeFalse();
  });
});

describe('FavoriteButton', () => {
  let auth: ReturnType<typeof fakeAuth>;
  let favorites: jasmine.SpyObj<FavoritesService>;
  let fixture: ComponentFixture<FavoriteButton>;
  let saved: boolean;

  const button = () => (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.favorite-toggle');

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  beforeEach(async () => {
    auth = fakeAuth();
    saved = false;
    favorites = jasmine.createSpyObj<FavoritesService>('FavoritesService', ['isFavorite', 'add', 'remove']);
    favorites.isFavorite.and.callFake(() => saved);
    favorites.add.and.callFake(async () => {
      saved = true;
    });
    favorites.remove.and.callFake(async () => {
      saved = false;
    });
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: auth },
        { provide: FavoritesService, useValue: favorites },
      ],
    });
    fixture = TestBed.createComponent(FavoriteButton);
    fixture.componentRef.setInput('locationId', 'loc-1');
    await settle();
  });

  it('is only there for signed-in users and toggles the favorite', async () => {
    expect(button()).toBeNull();

    auth.me.set(ME);
    await settle();
    expect(button()!.getAttribute('aria-pressed')).toBe('false');
    expect(button()!.getAttribute('aria-label')).toBe('Zu den Favoriten');

    button()!.click();
    await settle();
    expect(favorites.add).toHaveBeenCalledOnceWith('loc-1');
    expect(button()!.getAttribute('aria-pressed')).toBe('true');
    expect(button()!.classList).toContain('saved');

    button()!.click();
    await settle();
    expect(favorites.remove).toHaveBeenCalledOnceWith('loc-1');
    expect(button()!.getAttribute('aria-pressed')).toBe('false');
  });

  it('shows why a favorite could not be saved', async () => {
    auth.me.set(ME);
    favorites.add.and.rejectWith(
      new HttpErrorResponse({ status: 409, error: { title: 'Conflict', detail: 'An account can have at most 200 favorites. Remove one first.' } }),
    );
    await settle();

    button()!.click();
    await settle();

    expect(button()!.title).toBe('An account can have at most 200 favorites. Remove one first.');
    expect(button()!.disabled).toBeFalse();
  });
});

describe('FavoritesPage', () => {
  let favorites: jasmine.SpyObj<FavoritesService>;
  let auth: ReturnType<typeof fakeAuth>;
  let fixture: ComponentFixture<FavoritesPage>;

  const root = () => fixture.nativeElement as HTMLElement;

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  beforeEach(() => {
    auth = fakeAuth(ME);
    favorites = jasmine.createSpyObj<FavoritesService>('FavoritesService', ['list', 'isFavorite', 'add', 'remove']);
    favorites.isFavorite.and.returnValue(true);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: AuthService, useValue: auth }, { provide: FavoritesService, useValue: favorites }],
    });
  });

  afterEach(() => fixture.destroy());

  it('lists the favorites with their status and links to map and details', async () => {
    auth.hasPermission.and.returnValue(true);
    favorites.list.and.resolveTo(response('loc-1', 'loc-2'));
    fixture = TestBed.createComponent(FavoritesPage);
    await settle();

    const cards = Array.from(root().querySelectorAll('.favorite'));
    expect(cards.map((card) => text(card, 'h2'))).toEqual(['Parkgarage Stephansplatz', 'Ladestandort']);
    expect(text(cards[0], '.availability')).toBe('2 von 4 Ladepunkten frei · frei bis 150 kW');
    expect(cards[0].querySelector<HTMLElement>('.status-dot')!.style.background).toBe('var(--cg-power-hpc)');
    expect(text(cards[1], '.availability')).toBe('2 von 4 Ladepunkten frei · bis 22 kW');
    expect(text(cards[0], '.status-age')).toMatch(/^Status von 14\.10\.2026 \d\d:15$/);
    expect(cards[0].querySelector('.show-on-map')!.getAttribute('href')).toBe('/#map=17.00/48.20850/16.37310');
    expect(cards[0].querySelector('.details')!.getAttribute('href')).toBe('/locations/loc-1');
    expect(cards[0].querySelector('app-favorite-button')).not.toBeNull();
    expect(text(root(), '#favorites-meta')).toMatch(/^Stand \d\d:\d\d · aktualisiert sich jede Minute · 2 von höchstens 200$/);
    expect(text(root(), '#favorites-attribution')).toBe('Datenquelle: E-Control');
  });

  it('refreshes every minute and on demand, and tells when that fails', async () => {
    favorites.list.and.resolveTo(response());
    jasmine.clock().install();
    try {
      fixture = TestBed.createComponent(FavoritesPage);
      jasmine.clock().tick(FAVORITES_REFRESH_MS);
    } finally {
      jasmine.clock().uninstall();
    }
    await settle();
    expect(favorites.list).toHaveBeenCalledTimes(2);
    expect(text(root(), '#no-favorites')).toContain('Du hast noch keine Favoriten.');
    expect(root().querySelector('#favorites-attribution')).not.toBeNull();

    favorites.list.and.rejectWith(new Error('offline'));
    root().querySelector<HTMLButtonElement>('#refresh-favorites')!.click();
    await settle();
    expect(text(root(), '#favorites-error')).toContain('konnten nicht geladen werden');
  });

  it('shows no details link without evse:read and no attribution without sources', async () => {
    auth.hasPermission.and.returnValue(false);
    favorites.list.and.resolveTo({ ...response('loc-1'), meta: { returned: 1, max: 200, attribution: [] } });
    fixture = TestBed.createComponent(FavoritesPage);
    await settle();

    expect(root().querySelector('.favorite .details')).toBeNull();
    expect(root().querySelector('#favorites-attribution')).toBeNull();
  });

  it('shows a location without power', async () => {
    favorites.list.and.resolveTo({
      ...response(),
      data: [{ location: locationSummary({ maxPowerKw: null, maxAvailablePowerKw: null, statusObservedAt: null }), addedAt: '2026-10-14T08:00:00Z' }],
    });
    fixture = TestBed.createComponent(FavoritesPage);
    await settle();

    expect(text(root(), '.favorite .availability')).toBe('2 von 4 Ladepunkten frei');
    expect(text(root(), '.favorite .status-age')).toBe('Kein aktueller Status');
  });
});
