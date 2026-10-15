import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { AdminConflictsPage } from '../pages/admin-conflicts.page';
import { flush, text } from '../testing';
import { Conflict, ConflictsResponse, ConflictsService, describeConflict } from './conflicts.service';

function conflict(overrides: Partial<Conflict> = {}): Conflict {
  return {
    id: 'c-1',
    entityType: 'LOCATION',
    entityId: 'loc-1',
    entityLabel: 'Parkgarage Stephansplatz, Stephansplatz 1, Wien',
    attribute: 'identity',
    values: { source: 'ocm', externalId: '148527', candidates: ['loc-1', 'loc-2'] },
    detectedAt: '2026-10-15T08:00:00Z',
    resolvedAt: null,
    resolution: null,
    candidates: [],
    ...overrides,
  };
}

const response = (data: Conflict[], open = data.length): ConflictsResponse => ({ data, meta: { returned: data.length, limit: 200, open } });

describe('ConflictsService', () => {
  it('asks for open or resolved conflicts', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    const service = TestBed.inject(ConflictsService);

    const open = service.list(false);
    http.expectOne('/api/v1/conflicts?limit=200').flush(response([conflict()]));
    expect((await open).data.length).toBe(1);

    const resolved = service.list(true);
    http.expectOne('/api/v1/conflicts?limit=200&resolved=true').flush(response([]));
    expect((await resolved).meta.open).toBe(0);

    const merged = service.resolve('c-1', 'merge', 'loc-2');
    const request = http.expectOne('/api/v1/conflicts/c-1/resolve');
    expect([request.request.method, request.request.body]).toEqual(['POST', { action: 'merge', targetId: 'loc-2' }]);
    request.flush({ data: conflict({ resolvedAt: '2026-10-16T08:00:00Z' }) });
    expect((await merged).resolvedAt).toBe('2026-10-16T08:00:00Z');

    const kept = service.resolve('c-2', 'keep');
    const keep = http.expectOne('/api/v1/conflicts/c-2/resolve');
    expect(keep.request.body).toEqual({ action: 'keep', targetId: null });
    keep.flush({ data: conflict({ id: 'c-2' }) });
    await kept;
    http.verify();
  });

  it('describes the values in short German', () => {
    expect(describeConflict(conflict())).toBe('ocm 148527 passt zu 2 Standorten');
    expect(describeConflict(conflict({ attribute: 'evse_id', values: { stored: 'AT*TST*E1', ocm: 'AT-TST-E1', count: 2 } }))).toBe(
      'stored: AT*TST*E1 · ocm: AT-TST-E1 · count: 2',
    );
  });
});

describe('AdminConflictsPage', () => {
  let service: jasmine.SpyObj<ConflictsService>;
  let snackBar: jasmine.SpyObj<MatSnackBar>;
  let fixture: ComponentFixture<AdminConflictsPage>;
  const root = () => fixture.nativeElement as HTMLElement;

  async function render(): Promise<void> {
    fixture = TestBed.createComponent(AdminConflictsPage);
    await flush();
    await fixture.whenStable();
  }

  beforeEach(() => {
    service = jasmine.createSpyObj<ConflictsService>('ConflictsService', ['list', 'resolve']);
    snackBar = jasmine.createSpyObj<MatSnackBar>('MatSnackBar', ['open']);
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: ConflictsService, useValue: service }, { provide: MatSnackBar, useValue: snackBar }],
    });
  });

  it('lists the open conflicts with a link to what they concern and switches to the resolved ones', async () => {
    service.list.and.callFake(async (resolved) =>
      resolved
        ? response([conflict({ id: 'c-9', resolvedAt: '2026-10-16T08:00:00Z', resolution: 'merged by hand' })], 2)
        : response([
            conflict(),
            conflict({ id: 'c-2', entityType: 'EVSE', entityId: 'e-1', entityLabel: 'AT*TST*E1*1', attribute: 'evse_id', values: { stored: 'AT*TST*E1*1' } }),
            conflict({ id: 'c-3', entityId: null, entityLabel: null, attribute: 'other' }),
          ]),
    );
    await render();

    expect(text(root(), '#open-count')).toBe('3');
    const link = (id: string) => root().querySelector<HTMLAnchorElement>(`[data-conflict="${id}"] a`);
    expect(link('c-1')!.getAttribute('href')).toBe('/locations/loc-1');
    expect(link('c-2')!.getAttribute('href')).toBe('/evses/e-1');
    expect(link('c-3')).toBeNull();
    expect(text(root(), '[data-conflict="c-3"] td:nth-child(2)')).toBe('nicht mehr vorhanden');
    expect(Array.from(root().querySelectorAll('#conflicts tbody tr')).map((row) => text(row, 'td:nth-child(3)'))).toEqual([
      'Zuordnung mehrdeutig',
      'EVSE-ID-Schreibweise',
      'other',
    ]);
    expect(text(root(), '[data-conflict="c-1"] td:nth-child(5)')).toBe('eigener Standort');
    expect(text(root(), '[data-conflict="c-2"] td:nth-child(5)')).toBe('behalten');

    root().querySelectorAll<HTMLButtonElement>('#conflict-filter button')[1].click();
    await flush();
    await fixture.whenStable();
    expect(service.list).toHaveBeenCalledWith(true);
    expect(text(root(), '[data-conflict="c-9"] td:nth-child(5)')).toBe('16.10.2026 · merged by hand');
    expect(text(root(), '#open-count')).toBe('2');
  });

  it('assigns a record to a candidate, keeps it separate or keeps an EVSE-ID, then reloads', async () => {
    const identity = conflict({
      candidates: [
        { locationId: 'loc-1', label: 'Parkgarage Stephansplatz, Wien', distanceMeters: 18.4 },
        { locationId: 'loc-2', label: 'Teststation Dom, Wien', distanceMeters: null },
        { locationId: 'loc-3', label: null, distanceMeters: null },
      ],
    });
    const evseId = conflict({ id: 'c-2', entityType: 'EVSE', entityId: 'e-1', attribute: 'evse_id', values: { stored: 'AT*TST*E1*1' } });
    service.list.and.resolveTo(response([identity, evseId, conflict({ id: 'c-3', attribute: 'other' })]));
    service.resolve.and.resolveTo(conflict({ resolvedAt: '2026-10-16T08:00:00Z' }));
    await render();

    const candidate = (id: string) => root().querySelector<HTMLElement>(`[data-candidate="${id}"]`)!;
    expect(text(candidate('loc-1'), 'span')).toBe('18 m');
    expect(candidate('loc-2').querySelector('span')).toBeNull();
    // A candidate that no longer exists cannot be chosen.
    expect(text(candidate('loc-3'), 'a')).toBe('nicht mehr vorhanden');
    expect(candidate('loc-3').querySelector('button')).toBeNull();
    expect(text(root(), '[data-conflict="c-3"] td:nth-child(5)')).toBe('–');

    candidate('loc-2').querySelector<HTMLButtonElement>('.merge')!.click();
    await flush();
    await fixture.whenStable();
    expect(service.resolve).toHaveBeenCalledWith('c-1', 'merge', 'loc-2');
    expect(snackBar.open).toHaveBeenCalledWith('Datensatz dem Standort zugeordnet.', 'OK', { duration: 4000 });
    expect(service.list).toHaveBeenCalledTimes(2);

    root().querySelector<HTMLButtonElement>('[data-conflict="c-1"] .separate')!.click();
    await flush();
    await fixture.whenStable();
    expect(service.resolve).toHaveBeenCalledWith('c-1', 'separate', null);
    expect(snackBar.open).toHaveBeenCalledWith('Datensatz wird ein eigener Standort.', 'OK', { duration: 4000 });

    root().querySelector<HTMLButtonElement>('[data-conflict="c-2"] .keep')!.click();
    await flush();
    await fixture.whenStable();
    expect(service.resolve).toHaveBeenCalledWith('c-2', 'keep', null);
    expect(snackBar.open).toHaveBeenCalledWith('EVSE-ID bleibt wie gespeichert.', 'OK', { duration: 4000 });

    service.resolve.and.rejectWith(new HttpErrorResponse({ status: 422, error: { title: 'Validation failed', errors: { targetId: ['Must be one of the candidates.'] } } }));
    candidate('loc-1').querySelector<HTMLButtonElement>('.merge')!.click();
    await flush();
    await fixture.whenStable();
    expect(text(root(), '.error')).toBe('Must be one of the candidates.');
  });

  it('says when there are none and shows errors', async () => {
    service.list.and.resolveTo(response([]));
    await render();
    expect(text(root(), '#conflicts tbody')).toBe('Keine Konflikte.');

    service.list.and.rejectWith(new HttpErrorResponse({ status: 503, error: { title: 'Service unavailable', detail: 'Database down.' } }));
    root().querySelectorAll<HTMLButtonElement>('#conflict-filter button')[1].click();
    await flush();
    await fixture.whenStable();
    expect(text(root(), '.error')).toBe('Database down.');
  });
});
