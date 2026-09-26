// Вынесено из bottom-sheet.component.ts (см. докстринг там для полного объяснения) — переиспользуется
// и file-viewer'ом: оба открываются как оверлей без смены роута (@Input open, не Router), поэтому
// системная «назад» (браузер/мобильный жест) иначе видела бы их не существующими и уводила на
// предыдущий экран целиком, будто оверлея не было.

/**
 * Открытие добавляет одну фиктивную запись в историю; «назад» её просто снимает и закрывает
 * оверлей, оставляя пользователя на том экране, откуда он его открыл. Компонент вызывает `sync()`
 * на каждое изменение `open`, `onPopState()` из `@HostListener('window:popstate')` и `destroy()`
 * из `ngOnDestroy` — сам класс не трогает Angular-хуки, только состояние истории.
 */
export class HistoryDismissController {
  /** Этот оверлей (а не какой-то другой, если их несколько на странице) сам добавил запись
   * в историю — только тогда её и нужно снимать при закрытии не через «назад». */
  private pushedHistoryEntry = false;

  constructor(private readonly onDismissedByBack: () => void) {}

  sync(open: boolean): void {
    if (open && !this.pushedHistoryEntry) {
      history.pushState({ overlayDismiss: true }, '', window.location.href);
      this.pushedHistoryEntry = true;
    } else if (!open && this.pushedHistoryEntry) {
      // Закрыли не «назад» (крестик/бэкдроп/Escape) — саму фиктивную запись нужно убрать,
      // иначе следующее нажатие «назад» схлопнет её вникуда, а не уведёт на предыдущий экран.
      this.pushedHistoryEntry = false;
      this.popOwnEntry();
    }
  }

  /**
   * Снимает фиктивную запись ТОЛЬКО если она всё ещё верхняя. Если оверлей закрылся уже после
   * перехода на другой экран (пункт «Профиль» в листе «Ещё»: роутер добавил свою запись поверх нашей),
   * `history.back()` откатил бы этот переход — экран «не меняется». Тогда фиктивная запись остаётся
   * под новой: она указывает на прежний экран, лишнее «назад» просто вернёт туда, откуда пришли.
   */
  private popOwnEntry(): void {
    if (history.state?.overlayDismiss) history.back();
  }

  onPopState(open: boolean): void {
    if (open && this.pushedHistoryEntry) {
      this.pushedHistoryEntry = false;
      this.onDismissedByBack();
    }
  }

  destroy(): void {
    if (this.pushedHistoryEntry) {
      this.pushedHistoryEntry = false;
      this.popOwnEntry();
    }
  }
}
