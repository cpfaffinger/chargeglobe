import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attempt } from '../auth/problem';
import { SettingRow } from '../settings/setting-row';
import { Setting, SettingsService } from '../settings/settings.service';
import { SetupService } from '../setup/setup.service';

type Step = 'admin' | 'source' | 'mail' | 'done';

const STEPS: { step: Step; label: string }[] = [
  { step: 'admin', label: 'Administrator' },
  { step: 'source', label: 'Datenquelle' },
  { step: 'mail', label: 'E-Mail' },
  { step: 'done', label: 'Fertig' },
];

/** Scope of the settings of the E-Control adapter in the config service. */
const SOURCE_SCOPE = 'provider:econtrol';
const SOURCE_KEYS = ['provider.api_key', 'provider.referer', 'provider.enabled'];
const MAIL_KEYS = [
  'mail.smtp.host',
  'mail.smtp.port',
  'mail.smtp.security',
  'mail.smtp.username',
  'mail.smtp.password',
  'mail.from.address',
  'mail.from.name',
  'mail.enabled',
];

/**
 * Setup wizard. While the instance has no user (GET /api/v1/setup), every page leads here: the first step creates
 * the first administrator with the setup token from the API log and signs it in. The following steps store the
 * E-Control access and the SMTP server in the config service; they can be skipped and stay reachable for
 * settings:manage as onboarding of a running instance.
 */
@Component({
  imports: [ReactiveFormsModule, RouterLink, SettingRow],
  selector: 'app-setup',
  template: `
    <h2>Einrichtung</h2>
    <ol id="setup-steps">
      @for (entry of steps; track entry.step) {
        <li [class.current]="entry.step === step()">{{ entry.label }}</li>
      }
    </ol>
    @switch (step()) {
      @case ('admin') {
        <p>
          Willkommen bei chargeglobe. Diese Instanz hat noch kein Benutzerkonto. Lege jetzt das
          erste Administrator-Konto an; danach ist die Registrierung für alle anderen offen.
        </p>
        <p class="hint">
          Den Setup-Token schreibt die API beim Start in ihr Log:
          <code>docker compose logs api | grep "Setup required"</code>. Nach einem Neustart der API
          gilt ein neuer Token.
        </p>
        <form [formGroup]="form" (ngSubmit)="createAdmin()">
          <label
            >Setup-Token
            <input name="setupToken" formControlName="setupToken" autocomplete="off" required
          /></label>
          <label
            >E-Mail <input type="email" formControlName="email" autocomplete="username" required
          /></label>
          <label>
            Passwort (mindestens 10 Zeichen)
            <input
              type="password"
              name="password"
              formControlName="password"
              autocomplete="new-password"
              required
            />
          </label>
          <label>
            Passwort wiederholen
            <input
              type="password"
              name="repeat"
              formControlName="repeat"
              autocomplete="new-password"
              required
            />
          </label>
          <button type="submit" [disabled]="busy()">Administrator anlegen</button>
        </form>
      }
      @case ('source') {
        <h3>Datenquelle: Ladestellenverzeichnis der E-Control</h3>
        <p>
          Den kostenlosen API-Key gibt es nach der Registrierung unter
          <a href="https://admin.ladestellen.at" target="_blank" rel="noopener"
            >admin.ladestellen.at</a
          >. Er ist an eine Domain gebunden; trage sie als Referer ein und schalte die Quelle dann
          ein. Der Key wird verschlüsselt gespeichert und nicht wieder angezeigt.
        </p>
        @for (setting of rows(sourceKeys); track setting.key) {
          <app-setting-row
            [setting]="setting"
            [scope]="sourceScope"
            [canManage]="true"
            (changed)="replace($event)"
          />
        }
      }
      @case ('mail') {
        <h3>E-Mail-Versand (SMTP)</h3>
        <p>
          Ohne SMTP-Server bleiben Bestätigungs- und Passwort-Mails im Postausgang. Nach dem
          Speichern kannst du den Zugang unter <a routerLink="/admin/mail">E-Mail</a> mit einer
          Testmail prüfen.
        </p>
        @for (setting of rows(mailKeys); track setting.key) {
          <app-setting-row
            [setting]="setting"
            scope="global"
            [canManage]="true"
            (changed)="replace($event)"
          />
        }
      }
      @case ('done') {
        <p id="setup-done">
          Die Einrichtung ist abgeschlossen. Alle weiteren Einstellungen findest du unter
          <a routerLink="/admin/settings">Einstellungen</a>.
        </p>
        <p><a routerLink="/" id="to-map">Zur Karte</a></p>
      }
    }
    @if (step() === 'source' || step() === 'mail') {
      @if (busy()) {
        <p>Einstellungen werden geladen …</p>
      }
      <p class="setup-nav">
        @if (step() === 'mail') {
          <button type="button" id="back" (click)="step.set('source')">Zurück</button>
        }
        <button type="button" id="next" (click)="step.set(step() === 'source' ? 'mail' : 'done')">
          Weiter
        </button>
      </p>
    }
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
  `,
})
export class SetupPage {
  private readonly auth = inject(AuthService);
  private readonly setup = inject(SetupService);
  private readonly settingsService = inject(SettingsService);

  protected readonly steps = STEPS;
  protected readonly sourceKeys = SOURCE_KEYS;
  protected readonly mailKeys = MAIL_KEYS;
  protected readonly sourceScope = SOURCE_SCOPE;

  protected readonly step = signal<Step>(this.setup.required() ? 'admin' : 'source');
  protected readonly settings = signal<Setting[]>([]);
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    setupToken: '',
    email: '',
    password: '',
    repeat: '',
  });

  constructor() {
    if (this.step() === 'source') {
      void this.loadSettings();
    }
  }

  /** The settings of the catalog with the given keys, in that order. */
  protected rows(keys: string[]): Setting[] {
    return keys.flatMap((key) => this.settings().filter((setting) => setting.key === key));
  }

  protected replace(updated: Setting): void {
    this.settings.update((list) =>
      list.map((setting) => (setting.key === updated.key ? updated : setting)),
    );
  }

  protected async createAdmin(): Promise<void> {
    const { setupToken, email, password, repeat } = this.form.getRawValue();
    if (password !== repeat) {
      this.errors.set(['Die beiden Passwörter stimmen nicht überein.']);
      return;
    }
    await attempt(this.busy, this.errors, async () => {
      await this.auth.createInitialAdmin(setupToken.trim(), email.trim(), password);
      this.setup.done();
      this.step.set('source');
    });
    if (this.step() === 'source') {
      await this.loadSettings();
    }
  }

  private loadSettings(): Promise<void> {
    return attempt(this.busy, this.errors, async () =>
      this.settings.set(await this.settingsService.list()),
    );
  }
}
