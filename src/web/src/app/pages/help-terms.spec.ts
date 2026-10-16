import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { text } from '../testing';
import { HelpTermsPage, TERMS } from './help-terms.page';

describe('HelpTermsPage', () => {
  it('explains the terms from the park down to the connector, with examples', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(HelpTermsPage);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;

    expect(text(root, 'h1')).toBe('Begriffe');
    expect(Array.from(root.querySelectorAll('#terms dt')).map((term) => term.textContent!.trim())).toEqual([
      'Ladepark',
      'Ladestandort',
      'Ladestation',
      'Ladepunkt',
      'Anschluss',
    ]);
    expect(text(root, '#term-evse .term-example')).toBe('Beispiel: AT*EVN*E2454*1, 100 kW Gleichstrom.');
    expect(TERMS.every((term) => term.meaning.length > 40)).toBeTrue();
    expect(root.querySelector('#back')!.getAttribute('href')).toBe('/');
  });
});
