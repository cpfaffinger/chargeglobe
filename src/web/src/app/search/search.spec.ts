import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { flush, text, type } from '../testing';
import { LocationSearch, SEARCH_DELAY_MS } from './location-search';
import { MapFocus, SearchHit, SearchService } from './search.service';

const HIT: SearchHit = {
  type: 'EVSE',
  label: 'AT*TST*E1*1',
  detail: 'Stephansplatz 1, 1010 Wien',
  locationId: 'loc-1',
  latitude: 48.2085,
  longitude: 16.3731,
  evseId: 'AT*TST*E1*1',
};

describe('SearchService', () => {
  it('asks the public search for eight hits', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);

    const hits = TestBed.inject(SearchService).search('stephansplatz 1');
    http.expectOne('/api/v1/search?q=stephansplatz%201&limit=8').flush({ data: [HIT] });

    expect(await hits).toEqual([HIT]);
    http.verify();
  });
});

@Component({ template: 'elsewhere' })
class OtherPage {}

describe('LocationSearch', () => {
  let search: jasmine.SpyObj<SearchService>;
  let fixture: ComponentFixture<LocationSearch>;
  let router: Router;

  const root = () => fixture.nativeElement as HTMLElement;
  const options = () => Array.from(document.querySelectorAll<HTMLElement>('mat-option'));

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  /** Types into the field and lets the delayed search run. */
  async function enter(query: string): Promise<void> {
    const input = root().querySelector<HTMLInputElement>('#location-search')!;
    input.focus();
    type(root(), '#location-search', query);
    await new Promise((resolve) => setTimeout(resolve, SEARCH_DELAY_MS + 20));
    await settle();
  }

  beforeEach(async () => {
    search = jasmine.createSpyObj<SearchService>('SearchService', ['search']);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: OtherPage },
          { path: 'account', component: OtherPage },
        ]),
        { provide: SearchService, useValue: search },
      ],
    });
    router = TestBed.inject(Router);
    fixture = TestBed.createComponent(LocationSearch);
    await settle();
  });

  afterEach(() => fixture.destroy());

  it('searches after a pause, shows the hits and hands the chosen one to the map', async () => {
    search.search.and.resolveTo([HIT, { ...HIT, type: 'LOCATION', label: 'Parkgarage', detail: null, evseId: null }]);
    const chosen: SearchHit[] = [];
    fixture.componentRef.instance.chosen.subscribe((hit) => chosen.push(hit));
    await router.navigateByUrl('/account');

    await enter('  AT*TST ');

    expect(search.search).toHaveBeenCalledOnceWith('AT*TST');
    expect(options().map((option) => text(option, '.hit-label'))).toEqual(['AT*TST*E1*1', 'Parkgarage']);
    expect(text(options()[0], '.hit-detail')).toBe('Stephansplatz 1, 1010 Wien');
    expect(options()[1].querySelector('.hit-detail')).toBeNull();

    options()[0].click();
    await settle();

    expect(TestBed.inject(MapFocus).target()).toEqual({ locationId: 'loc-1', latitude: 48.2085, longitude: 16.3731 });
    expect(chosen).toEqual([HIT]);
    expect(router.url).toBe('/');
    expect(root().querySelector<HTMLInputElement>('#location-search')!.value).toBe('');
  });

  it('stays on the map page when a hit is chosen there', async () => {
    search.search.and.resolveTo([HIT]);
    await router.navigateByUrl('/');
    const navigate = spyOn(router, 'navigateByUrl').and.callThrough();

    await enter('AT*TST');
    options()[0].click();
    await settle();

    expect(navigate).not.toHaveBeenCalled();
  });

  it('says when nothing was found or the search failed', async () => {
    search.search.and.resolveTo([]);
    await enter('xyz');
    expect(options().map((option) => option.textContent!.trim())).toEqual(['Nichts gefunden.']);

    search.search.and.rejectWith(new Error('offline'));
    await enter('xyzz');
    expect(options().map((option) => option.textContent!.trim())).toEqual(['Die Suche ist gerade nicht erreichbar.']);
  });

  it('does not search for short queries and drops answers of older searches', async () => {
    let answer!: (hits: SearchHit[]) => void;
    let fail!: (error: Error) => void;
    search.search.and.returnValues(
      new Promise((resolve) => (answer = resolve)),
      new Promise((_resolve, reject) => (fail = reject)),
    );

    await enter('AT');
    await enter('A');
    answer([HIT]);
    await settle();
    expect(options()).toEqual([]);

    await enter('Wien');
    await enter('');
    fail(new Error('late'));
    await settle();
    expect(options()).toEqual([]);
    expect(search.search).toHaveBeenCalledTimes(2);
  });

  it('cancels a pending search when the field is removed', async () => {
    type(root(), '#location-search', 'Wien');
    fixture.destroy();
    await new Promise((resolve) => setTimeout(resolve, SEARCH_DELAY_MS + 20));

    expect(search.search).not.toHaveBeenCalled();
  });
});
