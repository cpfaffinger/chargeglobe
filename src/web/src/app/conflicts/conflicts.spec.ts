import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
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
  let fixture: ComponentFixture<AdminConflictsPage>;
  const root = () => fixture.nativeElement as HTMLElement;

  async function render(): Promise<void> {
    fixture = TestBed.createComponent(AdminConflictsPage);
    await flush();
    await fixture.whenStable();
  }

  beforeEach(() => {
    service = jasmine.createSpyObj<ConflictsService>('ConflictsService', ['list']);
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: ConflictsService, useValue: service }] });
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
    expect(text(root(), '[data-conflict="c-1"] td:nth-child(5)')).toBe('–');

    root().querySelectorAll<HTMLButtonElement>('#conflict-filter button')[1].click();
    await flush();
    await fixture.whenStable();
    expect(service.list).toHaveBeenCalledWith(true);
    expect(text(root(), '[data-conflict="c-9"] td:nth-child(5)')).toBe('16.10.2026 · merged by hand');
    expect(text(root(), '#open-count')).toBe('2');
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
