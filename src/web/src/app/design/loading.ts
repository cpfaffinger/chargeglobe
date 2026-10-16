import { Component, input } from '@angular/core';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

/**
 * What is being loaded, with a small spinner: the one look of every waiting state (lists, prices, histories, admin
 * tables). Announced to screen readers as a status.
 */
@Component({
  imports: [MatProgressSpinnerModule],
  selector: 'app-loading',
  host: { class: 'app-loading', role: 'status' },
  template: `<mat-spinner diameter="18" aria-hidden="true" /><span>{{ text() }}</span>`,
})
export class AppLoading {
  readonly text = input.required<string>();
}
