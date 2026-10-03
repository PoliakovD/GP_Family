import { Directive, HostListener } from '@angular/core';
import type { ConfirmService } from '../confirm/confirm.service';

/**
 * Помечает обёрнутую форму «грязной», как только пользователь что-то ввёл или выбрал (любое
 * input/change внутри). Контейнер перед закрытием (фон, крестик, Escape, «назад») спрашивает
 * подтверждение — раньше одно касание мимо листа стирало длинную форму.
 *
 *   <div appTrackDirty #dirty="trackDirty"> <app-some-form/> </div>
 *   if (await confirmDiscard(this.confirm, dirty.isDirty)) close();
 */
@Directive({
  selector: '[appTrackDirty]',
  standalone: true,
  exportAs: 'trackDirty',
})
export class TrackDirtyDirective {
  isDirty = false;

  @HostListener('input')
  @HostListener('change')
  markDirty(): void {
    this.isDirty = true;
  }

  reset(): void {
    this.isDirty = false;
  }
}

/** true — можно закрывать (форма чистая или пользователь согласился потерять введённое). */
export async function confirmDiscard(confirm: ConfirmService, dirty: boolean | undefined): Promise<boolean> {
  if (!dirty) return true;
  return confirm.confirm({
    title: 'Закрыть без сохранения?',
    message: 'Введённые данные пропадут.',
    confirmText: 'Закрыть',
    cancelText: 'Продолжить заполнение',
    danger: true,
  });
}
