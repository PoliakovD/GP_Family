import { Component, EventEmitter, Input, OnChanges, OnDestroy, OnInit, Output, TemplateRef, ViewChild, inject, ChangeDetectionStrategy } from '@angular/core';
import type { IconNode as LucideIconData } from 'lucide';
import { ContentPageService } from '../../services/content-page.service';
import type { Tone } from '../../shared/ui/tone';

/**
 * Справка на десктопе (`tier()==='wide'`): статья справочника (показатель, препарат) открывается
 * страницей в колонке контента — «‹ Назад к разделу» → плитка + «Справочник FamilyHub» + название →
 * статья (ContentPageService, рисует каркас). Узкие экраны монтируют то же тело в `shared/bottom-sheet`.
 * Монтируется потребителем через `@if` (своего `open` нет) — открытие = создание компонента.
 */
@Component({
  changeDetection: ChangeDetectionStrategy.Eager,
  selector: 'app-indicator-info-panel',
  standalone: true,
  // Десктоп: не панель справа, а страница в колонке контента (ContentPageService) — как открытая
  // запись. Содержимое рисует каркас; здесь только его шаблон и регистрация.
  template: `<ng-template #body><ng-content /></ng-template>`,
})
export class IndicatorInfoPanelComponent implements OnInit, OnChanges, OnDestroy {
  @Input() title = 'Справка';
  @Input() icon: LucideIconData | null = null;
  @Input() tone: Tone = 'neutral';
  /** Куда вернёт «‹ Назад» — раздел, откуда открыли статью. */
  @Input() backLabel = 'Назад';
  @Output() readonly closed = new EventEmitter<void>();

  @ViewChild('body', { static: true }) private body!: TemplateRef<unknown>;
  private readonly pages = inject(ContentPageService);

  ngOnInit(): void {
    this.pages.open({
      owner: this, title: this.title, kicker: 'Справочник FamilyHub', backLabel: this.backLabel,
      icon: this.icon, tone: this.tone, template: this.body, close: () => this.closed.emit(),
    });
  }

  ngOnChanges(): void {
    if (this.pages.isOpen(this)) this.pages.update(this, { title: this.title, icon: this.icon, tone: this.tone, backLabel: this.backLabel });
  }

  ngOnDestroy(): void {
    this.pages.close(this);
  }
}
