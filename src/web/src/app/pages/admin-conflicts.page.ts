import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { RouterLink } from '@angular/router';
import { attempt } from '../auth/problem';
import { Conflict, ConflictsService, describeConflict } from '../conflicts/conflicts.service';

const KINDS: Record<string, string> = {
  identity: 'Zuordnung mehrdeutig',
  evse_id: 'EVSE-ID-Schreibweise',
};

/**
 * Disagreements between sources that the merge did not decide (conflict:resolve): open ones by default, resolved ones on
 * demand, each with the location or EVSE it concerns.
 */
@Component({
  imports: [DatePipe, MatButtonToggleModule, RouterLink],
  selector: 'app-admin-conflicts',
  template: `
    <h1>Konflikte</h1>
    <p class="muted">
      Widersprüche zwischen Datenquellen, die der Abgleich nicht selbst entscheidet – etwa ein Datensatz, der zu mehreren
      Standorten in der Nähe passt. Offen: <span id="open-count">{{ open() }}</span>.
    </p>
    <mat-button-toggle-group id="conflict-filter" aria-label="Anzeige" hideSingleSelectionIndicator [value]="resolved()" (change)="show($event.value)">
      <mat-button-toggle [value]="false">offen</mat-button-toggle>
      <mat-button-toggle [value]="true">gelöst</mat-button-toggle>
    </mat-button-toggle-group>
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    <div class="table-box">
      <table class="data-table" id="conflicts">
        <thead>
          <tr><th scope="col">erkannt</th><th scope="col">betrifft</th><th scope="col">Art</th><th scope="col">Werte</th><th scope="col">gelöst</th></tr>
        </thead>
        <tbody>
          @for (conflict of conflicts(); track conflict.id) {
            <tr [attr.data-conflict]="conflict.id">
              <td>{{ conflict.detectedAt | date: 'dd.MM.yyyy HH:mm' }}</td>
              <td>
                @if (conflict.entityId && conflict.entityLabel) {
                  <a [routerLink]="link(conflict)">{{ conflict.entityLabel }}</a>
                } @else {
                  <span class="muted">nicht mehr vorhanden</span>
                }
              </td>
              <td>{{ kind(conflict.attribute) }}</td>
              <td>{{ describe(conflict) }}</td>
              <td>{{ conflict.resolvedAt ? (conflict.resolvedAt | date: 'dd.MM.yyyy') + ' · ' + conflict.resolution : '–' }}</td>
            </tr>
          } @empty {
            <tr><td colspan="5" class="muted">{{ busy() ? 'Wird geladen …' : 'Keine Konflikte.' }}</td></tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class AdminConflictsPage {
  private readonly service = inject(ConflictsService);

  protected readonly conflicts = signal<Conflict[]>([]);
  protected readonly open = signal(0);
  protected readonly resolved = signal(false);
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly describe = describeConflict;

  constructor() {
    void this.load();
  }

  protected kind(attribute: string): string {
    return KINDS[attribute] ?? attribute;
  }

  /** Page of the location or EVSE the conflict concerns. */
  protected link(conflict: Conflict): string[] {
    return [conflict.entityType === 'EVSE' ? '/evses' : '/locations', conflict.entityId!];
  }

  protected show(resolved: boolean): Promise<void> {
    this.resolved.set(resolved);
    return this.load();
  }

  private load(): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      const response = await this.service.list(this.resolved());
      this.conflicts.set(response.data);
      this.open.set(response.meta.open);
    });
  }
}
