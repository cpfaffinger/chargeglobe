import { Injectable, signal } from '@angular/core';

/**
 * The charging cards and apps (EMP keys) the signed-in user has. They belong to the account and are kept there through
 * PreferencesService; anonymous visitors have none.
 */
@Injectable({ providedIn: 'root' })
export class CardsStore {
  private readonly keys = signal<string[]>([]);

  readonly cards = this.keys.asReadonly();

  set(cards: string[]): void {
    this.keys.set(cards);
  }

  /** Adds or removes one card. */
  toggle(key: string, owned: boolean): void {
    this.keys.update((cards) => (owned ? [...cards.filter((card) => card !== key), key] : cards.filter((card) => card !== key)));
  }
}
