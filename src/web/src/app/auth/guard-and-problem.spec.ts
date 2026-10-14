import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { ME, fakeAuth, validationError } from '../testing';
import { AuthService } from './auth.service';
import { requireAnyPermission, requirePermission } from './permission.guard';
import { attempt, describeError, problemCode } from './problem';

describe('requirePermission', () => {
  let auth: ReturnType<typeof fakeAuth>;

  const run = (permission?: string) =>
    TestBed.runInInjectionContext(() =>
      requirePermission(permission)({} as ActivatedRouteSnapshot, { url: '/account?tab=keys' } as RouterStateSnapshot),
    );
  const url = (result: unknown) => TestBed.inject(Router).serializeUrl(result as UrlTree);

  beforeEach(() => {
    auth = fakeAuth();
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: auth }] });
  });

  it('redirects anonymous visitors to the login with the requested URL', () => {
    expect(url(run())).toBe('/login?returnUrl=%2Faccount%3Ftab%3Dkeys');
    expect(url(run('evse:read'))).toBe('/login?returnUrl=%2Faccount%3Ftab%3Dkeys');
  });

  it('lets any signed-in user pass when no permission is required', () => {
    auth.me.set(ME);

    expect(run()).toBeTrue();
  });

  it('checks the permission of signed-in users', () => {
    auth.me.set(ME);
    auth.hasPermission.and.callFake((permission) => permission === 'evse:read');

    expect(run('evse:read')).toBeTrue();
    expect(url(run('user:manage'))).toBe('/');
  });
});

describe('requireAnyPermission', () => {
  let auth: ReturnType<typeof fakeAuth>;

  const run = () =>
    TestBed.runInInjectionContext(() =>
      requireAnyPermission(['user:read', 'jobs:read'])({} as ActivatedRouteSnapshot, { url: '/admin' } as RouterStateSnapshot),
    );
  const url = (result: unknown) => TestBed.inject(Router).serializeUrl(result as UrlTree);

  beforeEach(() => {
    auth = fakeAuth();
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: auth }] });
  });

  it('redirects anonymous visitors to the login and lets in users with one of the permissions', () => {
    expect(url(run())).toBe('/login?returnUrl=%2Fadmin');

    auth.me.set(ME);
    auth.hasPermission.and.returnValue(false);
    expect(url(run())).toBe('/');

    auth.hasPermission.and.callFake((permission) => permission === 'jobs:read');
    expect(run()).toBeTrue();
  });
});

describe('problemCode', () => {
  it('reads the code of a problem response', () => {
    expect(problemCode(new HttpErrorResponse({ status: 403, error: { title: 'Forbidden', code: 'account_disabled' } }))).toBe('account_disabled');
    expect(problemCode(new HttpErrorResponse({ status: 401, error: { title: 'Unauthorized' } }))).toBeNull();
    expect(problemCode(new HttpErrorResponse({ status: 0 }))).toBeNull();
    expect(problemCode(new Error('offline'))).toBeNull();
  });
});

describe('describeError', () => {
  it('prefers the field errors of a validation problem', () => {
    const error = new HttpErrorResponse({
      status: 422,
      error: { title: 'Validation failed', errors: { email: ['Invalid.'], password: ['Too short.', 'Too weak.'] } },
    });

    expect(describeError(error)).toEqual(['Invalid.', 'Too short.', 'Too weak.']);
  });

  it('falls back to detail, then title, then a generic message', () => {
    const problem = (error: unknown) => new HttpErrorResponse({ status: 401, error });

    expect(describeError(problem({ title: 'Unauthorized', detail: 'Wrong password.' }))).toEqual(['Wrong password.']);
    expect(describeError(problem({ title: 'Unauthorized' }))).toEqual(['Unauthorized']);
    expect(describeError(problem(null))).toEqual(['Der Server ist nicht erreichbar.']);
    expect(describeError(new Error('boom'))).toEqual(['Der Server ist nicht erreichbar.']);
  });
});

describe('attempt', () => {
  it('is busy while the action runs and clears earlier errors', async () => {
    const busy = signal(false);
    const errors = signal(['old']);
    let busyDuringAction = false;

    await attempt(busy, errors, async () => {
      busyDuringAction = busy();
    });

    expect(busyDuringAction).toBeTrue();
    expect(busy()).toBeFalse();
    expect(errors()).toEqual([]);
  });

  it('collects the messages of a failed action', async () => {
    const busy = signal(false);
    const errors = signal<string[]>([]);

    await attempt(busy, errors, () => Promise.reject(validationError('Too short.')));

    expect(busy()).toBeFalse();
    expect(errors()).toEqual(['Too short.']);
  });
});
