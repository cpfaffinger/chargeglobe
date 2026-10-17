import { Injectable, computed, signal } from '@angular/core';

/** "emp/tariff", the key of an own tariff (listTariffs: emp and key). */
export const tariffKey = (emp: string, tariff: string): string => `${emp}/${tariff}`;

/**
 * The charging cards and apps the signed-in user has: the providers (EMP keys) and, chosen per provider, the own tariffs.
 * They belong to the account and are kept there through PreferencesService; anonymous visitors have none. With at least
 * one own tariff and onlyOwn (the default) the price lists of a charge point show only the own tariffs.
 */
@Injectable({ providedIn: 'root' })
export class CardsStore {
  private readonly keys = signal<string[]>([]);
  private readonly tariffKeys = signal<string[]>([]);
  private readonly only = signal(true);

  readonly cards = this.keys.asReadonly();
  readonly tariffs = this.tariffKeys.asReadonly();
  readonly onlyOwn = this.only.asReadonly();

  /** The lists show only the own tariffs: at least one is chosen and the user wants it so. */
  readonly filtering = computed(() => this.only() && this.tariffKeys().length > 0);

  /** What counts as own in the price lists: the own tariffs, or without them the own providers. */
  readonly ownKeys = computed(() => (this.tariffKeys().length > 0 ? this.tariffKeys() : this.keys()));

  set(cards: string[], tariffs: string[] = [], onlyOwn = true): void {
    this.keys.set(cards);
    this.tariffKeys.set(tariffs);
    this.only.set(onlyOwn);
  }

  /** Adds or removes one provider. */
  toggle(key: string, owned: boolean): void {
    this.keys.update((cards) => (owned ? [...cards.filter((card) => card !== key), key] : cards.filter((card) => card !== key)));
  }

  /** Adds or removes one tariff; the provider is own while it has an own tariff. */
  toggleTariff(emp: string, tariff: string, owned: boolean): void {
    const key = tariffKey(emp, tariff);
    const tariffs = owned ? [...this.tariffKeys().filter((item) => item !== key), key] : this.tariffKeys().filter((item) => item !== key);
    this.tariffKeys.set(tariffs);
    if (owned || !tariffs.some((item) => item.startsWith(`${emp}/`))) {
      this.toggle(emp, owned);
    }
  }

  setOnlyOwn(onlyOwn: boolean): void {
    this.only.set(onlyOwn);
  }
}
