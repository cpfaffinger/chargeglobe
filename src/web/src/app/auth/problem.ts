import { HttpErrorResponse } from '@angular/common/http';
import { WritableSignal } from '@angular/core';
import { AbstractControl } from '@angular/forms';

/** Messages of an RFC 9457 problem response: the field errors if present, else detail or title. */
export function describeError(error: unknown): string[] {
  const problem = error instanceof HttpErrorResponse ? error.error : null;
  const fieldErrors = Object.values<string[]>(problem?.errors ?? {}).flat();
  if (fieldErrors.length > 0) {
    return fieldErrors;
  }

  return [problem?.detail ?? problem?.title ?? 'Der Server ist nicht erreichbar.'];
}

/** The machine-readable `code` of a problem response (e.g. `account_disabled`), if there is one. */
export function problemCode(error: unknown): string | null {
  return error instanceof HttpErrorResponse ? (error.error?.code ?? null) : null;
}

/** Runs a form action with a busy flag and collects its error messages. */
export async function attempt(
  busy: WritableSignal<boolean>,
  errors: WritableSignal<string[]>,
  action: () => Promise<void>,
): Promise<void> {
  busy.set(true);
  errors.set([]);
  try {
    await action();
  } catch (error) {
    errors.set(describeError(error));
  } finally {
    busy.set(false);
  }
}

/** Like {@link attempt}, but an invalid form only shows its field errors (mat-error) and sends nothing. */
export function attemptValid(
  form: AbstractControl,
  busy: WritableSignal<boolean>,
  errors: WritableSignal<string[]>,
  action: () => Promise<void>,
): Promise<void> {
  if (form.invalid) {
    form.markAllAsTouched();
    return Promise.resolve();
  }
  return attempt(busy, errors, action);
}
