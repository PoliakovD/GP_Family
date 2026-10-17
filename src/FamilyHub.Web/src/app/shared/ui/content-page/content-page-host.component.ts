import { ChangeDetectionStrategy, Component, HostListener, effect, inject } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { ContentPageService } from '../../../services/content-page.service';
import { OverlayStackService } from '../../util/overlay-stack.service';
import { ContentPageComponent } from './content-page.component';

/**
 * Место страницы из ContentPageService в каркасе (внутри `.app-content`, вместо скрытого раздела).
 * Системная «назад» закрывает страницу (своя запись истории, как у нижнего листа), Escape — тоже,
 * если поверх не открыт другой оверлей. Прокрутка: при открытии — наверх, при закрытии — туда, где была.
 */
@Component({
  selector: 'app-content-page-host',
  standalone: true,
  imports: [ContentPageComponent, NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.Eager,
  template: `
    @if (pages.current(); as p) {
      <app-content-page [title]="p.title" [kicker]="p.kicker ?? null" [backLabel]="p.backLabel"
                        [icon]="p.icon ?? null" [tone]="p.tone ?? 'neutral'" [maxWidth]="p.maxWidth ?? '760px'"
                        (back)="p.close()">
        <ng-container *ngTemplateOutlet="p.template" />
      </app-content-page>
    }
  `,
})
export class ContentPageHostComponent {
  protected readonly pages = inject(ContentPageService);
  private readonly overlays = inject(OverlayStackService);

  private pushed = false;
  private savedScroll = 0;

  constructor() {
    effect(() => {
      const open = this.pages.current() !== null;
      if (open && !this.pushed) {
        this.savedScroll = window.scrollY;
        history.pushState({ contentPage: true }, '', location.href);
        this.pushed = true;
        queueMicrotask(() => window.scrollTo({ top: 0 }));
      } else if (!open && this.pushed) {
        this.pushed = false;
        if (history.state?.contentPage) history.back();
        const top = this.savedScroll;
        setTimeout(() => window.scrollTo({ top }));
      }
    });
  }

  @HostListener('window:popstate')
  onPopState(): void {
    const page = this.pages.current();
    if (!page || !this.pushed) return;
    this.pushed = false;
    page.close();
    // Владелец не закрылся (несохранённые данные, отказ в подтверждении) — вернуть свою запись истории.
    setTimeout(() => {
      if (this.pages.current() && !this.pushed) {
        history.pushState({ contentPage: true }, '', location.href);
        this.pushed = true;
      }
    });
  }

  @HostListener('document:keydown.escape', ['$event'])
  onEscape(event: Event): void {
    if (event.defaultPrevented || this.overlays.hasOpen()) return;
    this.pages.current()?.close();
  }
}
