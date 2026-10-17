import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { attempt } from '../auth/problem';
import { AppLoading } from '../design/loading';
import { Emp, Tariff, TariffsService } from '../tariffs/tariffs.service';
import { CardsStore, tariffKey } from './cards.store';

/** A provider with its current tariffs. */
interface EmpTariffs {
  emp: Emp;
  tariffs: Tariff[];
}

/**
 * Choice of the own charging cards and apps (price:read): per provider of GET /emps its current tariffs (GET /tariffs)
 * as checkboxes, and whether the price lists of a charge point show only these. The choice goes into the account with the
 * other preferences. A provider chosen before tariffs could be chosen stays until the user picks a tariff or removes it.
 */
@Component({
  imports: [AppLoading, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSlideToggleModule],
  selector: 'app-own-cards',
  template: `
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    <mat-slide-toggle id="only-own" [checked]="store.onlyOwn()" [disabled]="store.tariffs().length === 0" (change)="store.setOnlyOwn($event.checked)">
      Bei Ladepunkten nur meine Tarife zeigen
    </mat-slide-toggle>
    <p class="muted own-summary" id="own-summary">
      @if (store.tariffs().length === 0) {
        Noch kein Tarif gewählt – Ladepunkte zeigen alle Ladekarten und Apps.
      } @else {
        {{ store.tariffs().length === 1 ? '1 Tarif' : store.tariffs().length + ' Tarife' }} gewählt{{
          store.onlyOwn() ? ' – Ladepunkte zeigen nur diese, alle auf Klick.' : '.'
        }}
      }
    </p>
    <mat-form-field class="own-search" subscriptSizing="dynamic">
      <mat-label>Anbieter suchen</mat-label>
      <input matInput id="own-search" autocomplete="off" [value]="query()" (input)="query.set($any($event.target).value)" />
    </mat-form-field>
    <ul class="own-cards" aria-label="Meine Ladekarten und Tarife">
      @for (group of shown(); track group.emp.key) {
        <li>
          <details class="own-emp" [attr.data-card]="group.emp.key" [open]="ownTariffs(group) > 0 || legacy(group)">
            <summary>
              {{ group.emp.name }}
              @if (ownTariffs(group) > 0) {
                <span class="own-count">{{ ownTariffs(group) }} gewählt</span>
              }
            </summary>
            @if (legacy(group)) {
              <p class="legacy">
                Anbieter gewählt, Tarif noch offen.
                <button matButton type="button" class="remove-card" (click)="store.toggle(group.emp.key, false)">entfernen</button>
              </p>
            }
            @for (tariff of group.tariffs; track tariff.key) {
              <mat-checkbox
                [checked]="isOwn(tariff)"
                [attr.data-tariff]="key(tariff)"
                (change)="store.toggleTariff(tariff.emp, tariff.key, $event.checked)"
              >
                {{ tariff.name }}{{ tariff.monthlyFee ? ' · ' + fee(tariff.monthlyFee) + '/Monat' : '' }}
              </mat-checkbox>
            }
          </details>
        </li>
      } @empty {
        <li class="muted">
          @if (busy()) {
            <app-loading text="Anbieter werden geladen …" />
          } @else if (query()) {
            Kein Anbieter passt zur Suche.
          } @else {
            Noch keine Ladekarten-Anbieter erfasst.
          }
        </li>
      }
    </ul>
  `,
  styles: `
    .own-cards {
      list-style: none;
      margin: 0;
      padding: 0;
    }
    .own-emp mat-checkbox {
      display: block;
    }
    .own-count {
      margin-left: 6px;
      color: var(--mat-sys-primary);
      font: var(--mat-sys-label-small);
    }
    .own-search {
      width: 100%;
      margin-top: 8px;
    }
  `,
})
export class OwnCards {
  private readonly service = inject(TariffsService);
  protected readonly store = inject(CardsStore);
  protected readonly groups = signal<EmpTariffs[]>([]);
  protected readonly query = signal('');
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);

  /** The providers whose name matches the search; providers with own tariffs first. */
  protected readonly shown = computed(() => {
    const query = this.query().trim().toLowerCase();
    return this.groups()
      .filter((group) => !query || group.emp.name.toLowerCase().includes(query))
      .sort((left, right) => Number(this.ownTariffs(right) > 0) - Number(this.ownTariffs(left) > 0));
  });

  constructor() {
    void attempt(this.busy, this.errors, async () => {
      const [emps, tariffs] = await Promise.all([this.service.emps(), this.service.tariffs()]);
      this.groups.set(emps.map((emp) => ({ emp, tariffs: tariffs.filter((tariff) => tariff.emp === emp.key) })));
    });
  }

  protected key(tariff: Tariff): string {
    return tariffKey(tariff.emp, tariff.key);
  }

  protected isOwn(tariff: Tariff): boolean {
    return this.store.tariffs().includes(this.key(tariff));
  }

  protected ownTariffs(group: EmpTariffs): number {
    return group.tariffs.filter((tariff) => this.isOwn(tariff)).length;
  }

  /** The provider is chosen, but none of its tariffs (a choice from before tariffs could be chosen). */
  protected legacy(group: EmpTariffs): boolean {
    return this.store.cards().includes(group.emp.key) && this.ownTariffs(group) === 0;
  }

  protected fee(amount: number): string {
    return new Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR' }).format(amount);
  }
}
