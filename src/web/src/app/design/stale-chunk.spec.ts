import { DOCUMENT, ErrorHandler } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RELOAD, RELOADED_AT, RELOAD_COOLDOWN_MS, ReloadOnStaleChunk, isStaleChunkError } from './stale-chunk';

describe('ReloadOnStaleChunk', () => {
  const stale = () => new TypeError('Failed to fetch dynamically imported module: https://chargeglobe.net/chunk-ABCDEFGH.js');
  let reloads = 0;

  beforeEach(() => {
    reloads = 0;
    sessionStorage.removeItem(RELOADED_AT);
    spyOn(console, 'error');
    TestBed.configureTestingModule({
      providers: [
        { provide: ErrorHandler, useClass: ReloadOnStaleChunk },
        { provide: RELOAD, useValue: () => reloads++ },
      ],
    });
  });

  afterEach(() => sessionStorage.removeItem(RELOADED_AT));

  it('recognises the messages of the browsers, also wrapped as rejection or cause', () => {
    expect(isStaleChunkError(stale())).toBeTrue();
    expect(isStaleChunkError(new TypeError('error loading dynamically imported module: https://x/chunk-A.js'))).toBeTrue();
    expect(isStaleChunkError(new TypeError('Importing a module script failed.'))).toBeTrue();
    expect(isStaleChunkError('Failed to load module script: Expected a JavaScript module script')).toBeTrue();
    expect(isStaleChunkError(new Error('NG0750'))).toBeTrue();
    expect(isStaleChunkError({ rejection: stale() })).toBeTrue();
    expect(isStaleChunkError(new Error('navigation failed', { cause: stale() }))).toBeTrue();
    expect(isStaleChunkError(new Error('boom'))).toBeFalse();
    expect(isStaleChunkError(null)).toBeFalse();
    expect(isStaleChunkError(42)).toBeFalse();
    expect(isStaleChunkError({ message: 7 })).toBeFalse();
    // Wrapped deeper than five levels it is not looked at any more.
    let deep: unknown = stale();
    for (let level = 0; level < 6; level++) {
      deep = { cause: deep };
    }
    expect(isStaleChunkError(deep)).toBeFalse();
  });

  it('reloads once for a stale chunk and logs everything else', () => {
    const handler = TestBed.inject(ErrorHandler);

    handler.handleError(stale());
    expect(reloads).toBe(1);
    expect(sessionStorage.getItem(RELOADED_AT)).not.toBeNull();
    expect(console.error).not.toHaveBeenCalled();

    // Within the cooldown the error is logged instead, so that a broken release does not reload endlessly.
    handler.handleError({ rejection: stale() });
    expect(reloads).toBe(1);
    expect(console.error).toHaveBeenCalledTimes(1);

    handler.handleError(new Error('boom'));
    expect(reloads).toBe(1);
    expect(console.error).toHaveBeenCalledTimes(2);
  });

  it('reloads again once the cooldown is over', () => {
    sessionStorage.setItem(RELOADED_AT, String(Date.now() - RELOAD_COOLDOWN_MS - 1));

    TestBed.inject(ErrorHandler).handleError(stale());

    expect(reloads).toBe(1);
  });

  it('reloads even when the storage is blocked', () => {
    spyOn(Storage.prototype, 'getItem').and.throwError('blocked');

    TestBed.inject(ErrorHandler).handleError(stale());

    expect(reloads).toBe(1);
  });

  it('reloads the page of the document by default', () => {
    const reload = jasmine.createSpy('reload');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: DOCUMENT, useValue: { defaultView: { location: { reload } } } }] });

    TestBed.inject(RELOAD)();

    expect(reload).toHaveBeenCalledTimes(1);
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: DOCUMENT, useValue: { defaultView: null } }] });
    expect(() => TestBed.inject(RELOAD)()).not.toThrow();
  });
});
