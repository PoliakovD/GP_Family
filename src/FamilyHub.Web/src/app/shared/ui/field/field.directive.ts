import { AfterContentChecked, Directive, ElementRef, inject, input } from '@angular/core';

let nextId = 0;

/**
 * Поле формы (DESIGN §3 «Поля»): обёртка `<div appField [invalid]="…">` вокруг
 * `<label class="form-label">`, контрола (`input/select/textarea`) и подсказок `.field-hint`.
 *
 * Связывает их для скринридера — то, чего в приложении не было (аудит §7: aria-invalid 0 раз,
 * aria-describedby 1 раз): label[for] ↔ id контрола, aria-describedby → все видимые подсказки,
 * aria-invalid → `invalid`. Подсказки с ошибкой получают role="alert" при появлении.
 *
 * Разметку и стили не навязывает — класс `.field` даёт только вертикальный ритм.
 */
@Directive({
  selector: '[appField]',
  standalone: true,
  host: { class: 'field' },
})
export class FieldDirective implements AfterContentChecked {
  readonly invalid = input(false);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly baseId = `fh-field-${++nextId}`;

  ngAfterContentChecked(): void {
    const root = this.host.nativeElement;
    const control = root.querySelector<HTMLElement>('input, select, textarea');
    if (!control) return;

    if (!control.id) control.id = `${this.baseId}-control`;
    const label = root.querySelector<HTMLLabelElement>('label');
    if (label && !label.htmlFor && !label.contains(control)) label.htmlFor = control.id;

    const hints = Array.from(root.querySelectorAll<HTMLElement>('.field-hint'));
    hints.forEach((hint, i) => {
      if (!hint.id) hint.id = `${this.baseId}-hint-${i}`;
      if (hint.classList.contains('field-hint-error')) hint.setAttribute('role', 'alert');
    });
    const describedBy = hints.map((h) => h.id).join(' ');
    if (describedBy) control.setAttribute('aria-describedby', describedBy);
    else control.removeAttribute('aria-describedby');

    if (this.invalid()) control.setAttribute('aria-invalid', 'true');
    else control.removeAttribute('aria-invalid');
  }
}
