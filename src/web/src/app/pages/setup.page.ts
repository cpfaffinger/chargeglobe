import { Component, inject, signal, viewChild } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatStep, MatStepper, MatStepperModule } from '@angular/material/stepper';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attempt, attemptValid } from '../auth/problem';
import { AppIcon } from '../design/icon';
import { Viewport } from '../design/theme.service';
import { SettingRow } from '../settings/setting-row';
import { Setting, SettingsService } from '../settings/settings.service';
import { SetupService } from '../setup/setup.service';

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
 * Setup wizard as Material stepper (horizontal from 840 px, vertical below). While the instance has no user
 * (GET /api/v1/setup), every page leads here: the first step creates the first administrator with the setup token
 * from the API log and signs it in; the linear stepper opens the next steps only then. The following steps store the
 * E-Control access and the SMTP server in the config service; they are optional. Once the first administrator
 * exists, the wizard is closed (setupAccess); the settings stay in /admin/settings.
 */
@Component({
  imports: [
    AppIcon,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatStepperModule,
    ReactiveFormsModule,
    RouterLink,
    SettingRow,
  ],
  selector: 'app-setup',
  template: `
    <h1>Einrichtung</h1>
    <mat-stepper
      #stepper
      linear
      [orientation]="viewport.mobile() ? 'vertical' : 'horizontal'"
      aria-label="Schritte der Einrichtung"
    >
      <ng-template matStepperIcon="edit"><app-icon name="check" /></ng-template>
      <ng-template matStepperIcon="done"><app-icon name="check" /></ng-template>

      <mat-step #adminStep label="Administrator" [completed]="!setup.required()" [editable]="false">
        <div id="admin-step">
          <p>
            Diese Instanz hat noch kein Benutzerkonto. Lege jetzt das erste Administrator-Konto an; danach ist die
            Registrierung für alle anderen offen.
          </p>
          <p class="muted hint">
            Den Setup-Token schreibt die API beim Start in ihr Log:
            <code>docker compose logs api | grep "Setup required"</code>. Nach einem Neustart der API gilt ein neuer Token.
          </p>
          @for (error of errors(); track error) {
            <p class="error" role="alert">{{ error }}</p>
          }
          <form [formGroup]="form" (ngSubmit)="createAdmin()">
            <mat-form-field class="full">
              <mat-label>Setup-Token</mat-label>
              <input matInput name="setupToken" formControlName="setupToken" autocomplete="off" required />
              <mat-error>Den Setup-Token aus dem API-Log eingeben.</mat-error>
            </mat-form-field>
            <mat-form-field class="full">
              <mat-label>E-Mail</mat-label>
              <input matInput type="email" formControlName="email" autocomplete="username" required />
              <mat-error>Bitte eine gültige E-Mail-Adresse eingeben.</mat-error>
            </mat-form-field>
            <mat-form-field>
              <mat-label>Passwort</mat-label>
              <input matInput type="password" name="password" formControlName="password" autocomplete="new-password" required />
              <mat-hint>Mindestens 10 Zeichen.</mat-hint>
              <mat-error>Mindestens 10 Zeichen.</mat-error>
            </mat-form-field>
            <mat-form-field>
              <mat-label>Passwort wiederholen</mat-label>
              <input matInput type="password" name="repeat" formControlName="repeat" autocomplete="new-password" required />
              <mat-error>Bitte das Passwort wiederholen.</mat-error>
            </mat-form-field>
            <div class="step-actions full">
              <button matButton="filled" type="submit" [disabled]="busy()">Administrator anlegen</button>
            </div>
          </form>
        </div>
      </mat-step>

      <mat-step label="Datenquelle" optional>
        <ng-template matStepContent>
          <div id="source-step">
            <h2>Ladestellenverzeichnis der E-Control</h2>
            <p>
              Den kostenlosen API-Key gibt es nach der Registrierung unter
              <a href="https://admin.ladestellen.at" target="_blank" rel="noopener">admin.ladestellen.at</a>. Er ist an
              eine Domain gebunden; trage sie als Referer ein und schalte die Quelle dann ein. Der Key wird verschlüsselt
              gespeichert und nicht wieder angezeigt.
            </p>
            @if (busy()) {
              <mat-progress-bar mode="indeterminate" aria-label="Einstellungen werden geladen" />
            }
            @for (error of errors(); track error) {
              <p class="error" role="alert">{{ error }}</p>
            }
            @for (setting of rows(sourceKeys); track setting.key) {
              <app-setting-row [setting]="setting" [scope]="sourceScope" [canManage]="true" (changed)="replace($event)" />
            }
            <div class="step-actions">
              <button matButton="filled" type="button" id="next-source" matStepperNext>Weiter</button>
            </div>
          </div>
        </ng-template>
      </mat-step>

      <mat-step label="E-Mail" optional>
        <ng-template matStepContent>
          <div id="mail-step">
            <h2>E-Mail-Versand (SMTP)</h2>
            <p>
              Ohne SMTP-Server bleiben Bestätigungs- und Passwort-Mails im Postausgang. Nach dem Speichern kannst du den
              Zugang unter <a routerLink="/admin/mail">E-Mail</a> mit einer Testmail prüfen.
            </p>
            @for (setting of rows(mailKeys); track setting.key) {
              <app-setting-row [setting]="setting" scope="global" [canManage]="true" (changed)="replace($event)" />
            }
            <div class="step-actions">
              <button matButton type="button" id="back-mail" matStepperPrevious>Zurück</button>
              <button matButton="filled" type="button" id="next-mail" matStepperNext>Weiter</button>
            </div>
          </div>
        </ng-template>
      </mat-step>

      <mat-step label="Fertig">
        <ng-template matStepContent>
          <div id="done-step">
            <p id="setup-done">
              Die Einrichtung ist abgeschlossen. Alle weiteren Einstellungen findest du unter
              <a routerLink="/admin/settings">Einstellungen</a>.
            </p>
            <div class="step-actions">
              <a matButton="filled" routerLink="/" id="to-map">Zur Karte</a>
            </div>
          </div>
        </ng-template>
      </mat-step>
    </mat-stepper>
  `,
})
export class SetupPage {
  private readonly auth = inject(AuthService);
  protected readonly setup = inject(SetupService);
  protected readonly viewport = inject(Viewport);
  private readonly settingsService = inject(SettingsService);
  private readonly stepper = viewChild.required<MatStepper>('stepper');
  private readonly adminStep = viewChild.required<MatStep>('adminStep');

  protected readonly sourceKeys = SOURCE_KEYS;
  protected readonly mailKeys = MAIL_KEYS;
  protected readonly sourceScope = SOURCE_SCOPE;

  protected readonly settings = signal<Setting[]>([]);
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    setupToken: ['', Validators.required],
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(10)]],
    repeat: ['', Validators.required],
  });

  /** The settings of the catalog with the given keys, in that order. */
  protected rows(keys: string[]): Setting[] {
    return keys.flatMap((key) => this.settings().filter((setting) => setting.key === key));
  }

  protected replace(updated: Setting): void {
    this.settings.update((list) => list.map((setting) => (setting.key === updated.key ? updated : setting)));
  }

  protected async createAdmin(): Promise<void> {
    const { setupToken, email, password, repeat } = this.form.getRawValue();
    if (this.form.valid && password !== repeat) {
      this.errors.set(['Die beiden Passwörter stimmen nicht überein.']);
      return;
    }
    let created = false;
    await attemptValid(this.form, this.busy, this.errors, async () => {
      await this.auth.createInitialAdmin(setupToken.trim(), email.trim(), password);
      this.setup.done();
      created = true;
    });
    if (created) {
      // The linear stepper only moves on once the first step counts as completed.
      this.adminStep().completed = true;
      this.stepper().next();
      await this.loadSettings();
    }
  }

  private loadSettings(): Promise<void> {
    return attempt(this.busy, this.errors, async () => this.settings.set(await this.settingsService.list()));
  }
}
