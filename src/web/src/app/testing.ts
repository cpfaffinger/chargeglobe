import { HttpErrorResponse } from '@angular/common/http';
import { computed, signal } from '@angular/core';
import { AuthService, Me } from './auth/auth.service';

/** Shared helpers for the specs. */

export const ME: Me = {
  id: '01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4d',
  email: 'anna@example.org',
  roles: ['User'],
  permissions: ['apikey:manage', 'evse:read', 'price:read', 'route:read'],
  authenticatedVia: 'bearer',
};

/** Lets pending promise chains and timers of the code under test run. */
export const flush = (): Promise<void> => new Promise((resolve) => setTimeout(resolve));

/** AuthService double: spies for the methods, writable signal for the signed-in user. */
export function fakeAuth(me: Me | null = null) {
  const current = signal<Me | null>(me);
  const spies = jasmine.createSpyObj<AuthService>('AuthService', [
    'login',
    'logout',
    'register',
    'confirmEmail',
    'requestPasswordReset',
    'resetPassword',
    'refresh',
    'accessToken',
    'hasPermission',
  ]);
  return Object.assign(spies, { me: current, isAuthenticated: computed(() => current() !== null) });
}

/** A 422 problem response with one field error. */
export const validationError = (message: string): HttpErrorResponse =>
  new HttpErrorResponse({ status: 422, error: { title: 'Validation failed', errors: { field: [message] } } });

export function type(root: Element, selector: string, value: string): void {
  const input = root.querySelector<HTMLInputElement>(selector)!;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

export function submit(root: Element): void {
  root.querySelector('form')!.dispatchEvent(new Event('submit'));
}

export const text = (root: Element, selector: string): string | undefined =>
  root.querySelector(selector)?.textContent?.replace(/\s+/g, ' ').trim();
