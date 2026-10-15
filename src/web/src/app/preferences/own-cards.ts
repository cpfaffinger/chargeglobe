import { Component, inject, signal } from '@angular/core';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { attempt } from '../auth/problem';
import { Emp, TariffsService } from '../tariffs/tariffs.service';
import { CardsStore } from './cards.store';

/**
 * Choice of the own charging cards and apps (price:read): one checkbox per EMP of GET /emps. The choice goes into the
 * account with the other preferences; the price comparisons highlight these cards.
 */
@Component({
  imports: [MatCheckboxModule],
  selector: 'app-own-cards',
  template: `
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    <ul class="own-cards" aria-label="Meine Ladekarten">
      @for (emp of emps(); track emp.key) {
        <li>
          <mat-checkbox [checked]="store.cards().includes(emp.key)" [attr.data-card]="emp.key" (change)="store.toggle(emp.key, $event.checked)">
            {{ emp.name }}
          </mat-checkbox>
        </li>
      } @empty {
        <li class="muted">{{ busy() ? 'Anbieter werden geladen …' : 'Noch keine Ladekarten-Anbieter erfasst.' }}</li>
      }
    </ul>
  `,
  styles: `
    .own-cards {
      list-style: none;
      margin: 0;
      padding: 0;
    }
  `,
})
export class OwnCards {
  private readonly tariffs = inject(TariffsService);
  protected readonly store = inject(CardsStore);
  protected readonly emps = signal<Emp[]>([]);
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);

  constructor() {
    void attempt(this.busy, this.errors, async () => this.emps.set(await this.tariffs.emps()));
  }
}
