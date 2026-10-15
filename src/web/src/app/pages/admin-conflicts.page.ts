import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { attempt } from '../auth/problem';
import { Conflict, ConflictAction, ConflictsService, describeConflict } from '../conflicts/conflicts.service';

const KINDS: Record<string, string> = {
  identity: 'Zuordnung mehrdeutig',
  evse_id: 'EVSE-ID-Schreibweise',
};

const DONE: Record<ConflictAction, string> = {
  merge: 'Datensatz dem Standort zugeordnet.',
  separate: 'Datensatz wird ein eigener Standort.',
  keep: 'EVSE-ID bleibt wie gespeichert.',
};

/**
 * Disagreements between sources that the merge did not decide (conflict:resolve): open ones by default, resolved ones on
 * demand, each with the location or EVSE it concerns. An open identity conflict lists its candidates with distance and
 * can be assigned to one of them or kept as a location of its own; an evse_id conflict can be kept.
 */
@Component({
  imports: [DatePipe, DecimalPipe, MatButtonModule, MatButtonToggleModule, RouterLink],
  selector: 'app-admin-conflicts',
  styles: `
    .candidates {
      list-style: none;
      margin: 4px 0 0;
      padding: 0;
    }
    .candidates li {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px 8px;
    }
  `,
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
              <td>
                {{ describe(conflict) }}
                @if (conflict.candidates.length > 0) {
                  <ul class="candidates">
                    @for (candidate of conflict.candidates; track candidate.locationId) {
                      <li [attr.data-candidate]="candidate.locationId">
                        <a [routerLink]="['/locations', candidate.locationId]">{{ candidate.label ?? 'nicht mehr vorhanden' }}</a>
                        @if (candidate.distanceMeters !== null) {
                          <span class="muted">{{ candidate.distanceMeters | number: '1.0-0' }} m</span>
                        }
                        @if (!conflict.resolvedAt && candidate.label) {
                          <button matButton="tonal" type="button" class="merge" [disabled]="busy()" (click)="resolve(conflict, 'merge', candidate.locationId)">
                            diesem Standort zuordnen
                          </button>
                        }
                      </li>
                    }
                  </ul>
                }
              </td>
              <td>
                @if (conflict.resolvedAt) {
                  {{ conflict.resolvedAt | date: 'dd.MM.yyyy' }} · {{ conflict.resolution }}
                } @else if (conflict.attribute === 'identity') {
                  <button matButton type="button" class="separate" [disabled]="busy()" (click)="resolve(conflict, 'separate')">eigener Standort</button>
                } @else if (conflict.attribute === 'evse_id') {
                  <button matButton type="button" class="keep" [disabled]="busy()" (click)="resolve(conflict, 'keep')">behalten</button>
                } @else {
                  –
                }
              </td>
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
  private readonly snackBar = inject(MatSnackBar);

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

  protected resolve(conflict: Conflict, action: ConflictAction, targetId: string | null = null): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      await this.service.resolve(conflict.id, action, targetId);
      this.snackBar.open(DONE[action], 'OK', { duration: 4000 });
      await this.reload();
    });
  }

  private load(): Promise<void> {
    return attempt(this.busy, this.errors, () => this.reload());
  }

  private async reload(): Promise<void> {
    const response = await this.service.list(this.resolved());
    this.conflicts.set(response.data);
    this.open.set(response.meta.open);
  }
}
