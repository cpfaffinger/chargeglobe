import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { attempt } from '../auth/problem';
import { CrawlerRun, CrawlerService, CrawlerStatus, ProviderCrawlStatus } from '../crawler/crawler.service';
import { AppIcon } from '../design/icon';
import { AppLoading } from '../design/loading';

/** Polling interval of the page; the API is meant for it. */
export const CRAWLER_REFRESH_MS = 5000;

const JOB_LABELS: Record<string, string> = {
  INVENTORY_FULL: 'Inventar',
  STATUS_POLL: 'Status',
  TILE_PLAN: 'Abfragepunkte',
};

/**
 * Live view of the crawlers (jobs:read): per source budget, pause, query points and plans, the ingest queue
 * and the latest runs. Refreshes itself every five seconds.
 */
@Component({
  imports: [AppLoading, AppIcon, DatePipe, DecimalPipe, MatButtonModule, MatCardModule, MatChipsModule, MatProgressBarModule, MatTableModule, RouterLink],
  selector: 'app-admin-crawler',
  template: `
    <h1>Crawler</h1>
    <p class="muted">
      Aktualisiert sich alle {{ refreshSeconds }} Sekunden. Jede Quelle hat ein Abrufbudget pro Stunde
      (<code>provider.request_budget_per_hour</code>, <a routerLink="/admin/settings">Einstellungen</a>); nach Fehlern pausiert sie
      mit wachsender Wartezeit.
    </p>
    <div class="crawler-actions">
      <button matButton type="button" id="reload" [disabled]="loading()" (click)="load()"><app-icon name="refresh" />Aktualisieren</button>
      @if (loading()) {
        <app-loading id="crawler-loading" text="Status wird geladen …" />
      } @else if (status(); as current) {
        <span class="muted" id="updated">Stand {{ current.at | date: 'HH:mm:ss' }}</span>
      }
    </div>
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }

    @if (status(); as current) {
      @for (provider of current.providers; track provider.key) {
        <mat-card appearance="outlined" class="crawler-source" [id]="'source-' + provider.key">
          <mat-card-header>
            <mat-card-title>{{ provider.name ?? provider.key }}</mat-card-title>
          </mat-card-header>
          <mat-card-content>
            <mat-chip-set aria-label="Zustand der Quelle">
              <mat-chip class="state" [class.ok]="provider.enabled" [class.off]="!provider.enabled">{{ provider.enabled ? 'aktiv' : 'ausgeschaltet' }}</mat-chip>
              @if (provider.pause; as pause) {
                <mat-chip class="state paused" id="pause">
                  @if (pause.blocked) {
                    gesperrt bis zur nächsten Einstellungsänderung
                  } @else {
                    pausiert bis {{ pause.until | date: 'HH:mm' }}
                  }
                </mat-chip>
              }
            </mat-chip-set>
            @if (provider.pause?.reason; as reason) {
              <p class="muted">Letzter Fehler ({{ provider.pause!.failures }}× in Folge): {{ reason }}</p>
            }

            <p class="budget">{{ provider.requestsLastHour }} / {{ provider.budgetPerHour }} Abrufe in der letzten Stunde</p>
            <mat-progress-bar mode="determinate" [value]="usage(provider)" aria-label="Abrufbudget der letzten Stunde" />

            <p class="tiles" id="tiles">
              {{ provider.tiles.total }} Abfragepunkte · {{ provider.tiles.due }} fällig · {{ provider.tiles.hot }} lebhaft ·
              {{ provider.tiles.normal }} normal · {{ provider.tiles.quiet }} ruhig · {{ provider.tiles.onDemand }} angefordert
              @if (provider.tiles.oldestPoll) {
                · ältester Abruf {{ provider.tiles.oldestPoll | date: 'HH:mm' }}
              }
            </p>

            <table class="plans">
              <thead>
                <tr><th>Plan</th><th>Regionen</th><th>nächster Lauf</th><th>letztes Ergebnis</th></tr>
              </thead>
              <tbody>
                @for (plan of provider.plans; track plan.jobType + plan.target) {
                  <tr>
                    <td>{{ job(plan.jobType) }}</td>
                    <td>{{ plan.target }}</td>
                    <td>{{ plan.leasedBy ? 'läuft (' + plan.leasedBy + ')' : (plan.nextRunAt | date: 'dd.MM. HH:mm') }}</td>
                    <td>{{ plan.lastResult ?? '–' }}</td>
                  </tr>
                } @empty {
                  <tr><td colspan="4">Keine Pläne – die Quelle ist ausgeschaltet oder hat keine Regionen.</td></tr>
                }
              </tbody>
            </table>
          </mat-card-content>
        </mat-card>
      }
      <p id="queue">
        Ingest-Warteschlange: {{ current.queue.pending | number }} offen, {{ current.queue.failed | number }} mit Fehler
      </p>
    }

    <section id="runs">
      <h2>Letzte Läufe</h2>
      <div class="table-box">
        <table mat-table [dataSource]="runs()">
          <ng-container matColumnDef="startedAt">
            <th mat-header-cell *matHeaderCellDef>Start</th>
            <td mat-cell *matCellDef="let run">{{ run.startedAt | date: 'HH:mm:ss' }}</td>
          </ng-container>
          <ng-container matColumnDef="job">
            <th mat-header-cell *matHeaderCellDef>Job</th>
            <td mat-cell *matCellDef="let run">{{ job(run.jobType) }} · {{ run.provider }}</td>
          </ng-container>
          <ng-container matColumnDef="target">
            <th mat-header-cell *matHeaderCellDef>Ziel</th>
            <td mat-cell *matCellDef="let run">{{ run.target }}</td>
          </ng-container>
          <ng-container matColumnDef="requests">
            <th mat-header-cell *matHeaderCellDef>Abrufe</th>
            <td mat-cell *matCellDef="let run">{{ run.requests }}</td>
          </ng-container>
          <ng-container matColumnDef="records">
            <th mat-header-cell *matHeaderCellDef>Datensätze</th>
            <td mat-cell *matCellDef="let run">{{ run.records }}</td>
          </ng-container>
          <ng-container matColumnDef="result">
            <th mat-header-cell *matHeaderCellDef>Ergebnis</th>
            <td mat-cell *matCellDef="let run" class="result">
              <span class="run-result" [class]="'run-result ' + run.result.toLowerCase()">{{ run.result }}</span>
              @if (run.error) {
                <span class="note">{{ run.error }}</span>
              }
              @if (run.cooldownUntil) {
                <span class="note">Pause bis {{ run.cooldownUntil | date: 'HH:mm' }}</span>
              }
            </td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
          <tr class="mat-mdc-row no-data" *matNoDataRow>
            <td class="mat-mdc-cell" [attr.colspan]="columns.length" id="runs-empty">Noch keine Läufe.</td>
          </tr>
        </table>
      </div>
    </section>
  `,
})
export class AdminCrawlerPage {
  private readonly crawler = inject(CrawlerService);

  protected readonly refreshSeconds = CRAWLER_REFRESH_MS / 1000;
  protected readonly columns = ['startedAt', 'job', 'target', 'requests', 'records', 'result'];
  protected readonly status = signal<CrawlerStatus | null>(null);
  protected readonly runs = signal<CrawlerRun[]>([]);
  protected readonly loading = signal(false);
  protected readonly errors = signal<string[]>([]);

  constructor() {
    void this.load();
    const timer = setInterval(() => void this.load(), CRAWLER_REFRESH_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  /** Status and runs together; a refresh while one is running is skipped. */
  protected load(): Promise<void> {
    if (this.loading()) {
      return Promise.resolve();
    }

    return attempt(this.loading, this.errors, async () => {
      const [status, runs] = await Promise.all([this.crawler.status(), this.crawler.runs()]);
      this.status.set(status);
      this.runs.set(runs);
    });
  }

  protected job(jobType: string): string {
    return JOB_LABELS[jobType] ?? jobType;
  }

  protected usage(provider: ProviderCrawlStatus): number {
    return Math.min(100, (provider.requestsLastHour / provider.budgetPerHour) * 100);
  }
}
