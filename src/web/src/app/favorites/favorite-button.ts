import { Component, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../auth/auth.service';
import { AppIcon } from '../design/icon';
import { describeError } from '../auth/problem';
import { FavoritesService } from './favorites.service';

/** Star to add a location to the favorites or remove it; only for signed-in users. */
@Component({
  imports: [AppIcon, MatButtonModule],
  selector: 'app-favorite-button',
  template: `
    @if (auth.isAuthenticated()) {
      @let saved = favorites.isFavorite(locationId());
      <button
        matIconButton
        type="button"
        class="favorite-toggle"
        [class.saved]="saved"
        [disabled]="busy()"
        [attr.aria-pressed]="saved"
        [attr.aria-label]="saved ? 'Aus den Favoriten entfernen' : 'Zu den Favoriten'"
        [title]="error() ?? (saved ? 'Aus den Favoriten entfernen' : 'Zu den Favoriten')"
        (click)="toggle(saved)"
      >
        <app-icon [name]="saved ? 'star' : 'star_border'" />
      </button>
    }
  `,
})
export class FavoriteButton {
  protected readonly auth = inject(AuthService);
  protected readonly favorites = inject(FavoritesService);

  readonly locationId = input.required<string>();

  protected readonly busy = signal(false);
  /** Reason of a refused change (e.g. the maximum of favorites), shown as tooltip. */
  protected readonly error = signal<string | null>(null);

  protected async toggle(saved: boolean): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await (saved ? this.favorites.remove(this.locationId()) : this.favorites.add(this.locationId()));
    } catch (error) {
      this.error.set(describeError(error)[0]);
    } finally {
      this.busy.set(false);
    }
  }
}
