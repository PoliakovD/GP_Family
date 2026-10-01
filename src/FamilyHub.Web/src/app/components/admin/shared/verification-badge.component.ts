import { Component, Input } from '@angular/core';
import type { KbVerificationStatusValue } from '../../../services/admin-api.service';
import { verificationLabel, verificationTagClass } from '../admin-review/review-helpers';

/**
 * Бейдж статуса проверки записи справочника (ADR-0018) — ВНУТРЕННИЙ маркер, живёт только в админке:
 * пользовательский фронт и API его не получают. «Не проверено» и «устарело» (payload изменился после проверки)
 * — жёлтый, проверено — зелёный, знание эксперта — акцентный.
 */
@Component({
  selector: 'app-verification-badge',
  template: `<span [class]="tagClass" [title]="title">{{ label }}</span>`,
})
export class VerificationBadgeComponent {
  @Input({ required: true }) status!: KbVerificationStatusValue;
  @Input() stale = false;

  get label(): string {
    return verificationLabel(this.status, this.stale);
  }

  get tagClass(): string {
    return `tag ${verificationTagClass(this.status, this.stale)}`;
  }

  get title(): string {
    return this.stale
      ? 'Запись проверяли, но её содержимое с тех пор изменилось — проверка устарела.'
      : 'Внутренний статус проверки записи человеком — пользователям не показывается.';
  }
}
