import { Component, ElementRef, HostListener, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { BreakpointService } from '../../services/breakpoint.service';
import { BackgroundJobsStateService } from '../../services/background-jobs-state.service';
import { BottomSheetComponent } from '../bottom-sheet/bottom-sheet.component';
import { relatedKindBasePath } from '../util/related-kind-route';
import { pluralizeRu } from '../util/pluralize';
import type { ActiveJobItem, ActiveJobsGroup } from '../../models/types';

/** Одна секция выпадающего списка — заголовок + иконка конвейера + сама группа (§4 плана). */
interface JobSection {
  title: string;
  icon: string;
  group: ActiveJobsGroup;
}

/**
 * Глобальный индикатор фоновых процессов (§4 плана «живой конвейер») — бейдж уже стоит рядом с
 * пунктом «Здоровье» (см. app.component.html), это САМ выпадающий список за ним: по секции на
 * каждый непустой ActiveJobsGroup, с клик-через на конкретную запись/аптечку, когда она ещё
 * существует. Структура — тот же приём двух обёрток, что и shared/action-menu: десктоп-попап
 * (BreakpointService.tier()==='wide') или shared/bottom-sheet на более узких экранах. Само
 * содержимое не рендерится вовсе, когда фоновых задач нет (totalActive()===0) — размещать
 * компонент можно без внешней условной логики на стороне вызывающего шаблона.
 */
@Component({
  selector: 'app-background-jobs-dropdown',
  standalone: true,
  imports: [BottomSheetComponent],
  templateUrl: './background-jobs-dropdown.component.html',
  styleUrl: './background-jobs-dropdown.component.scss',
})
export class BackgroundJobsDropdownComponent {
  readonly jobs = inject(BackgroundJobsStateService);
  private readonly breakpoints = inject(BreakpointService);
  private readonly router = inject(Router);
  private readonly host = inject(ElementRef<HTMLElement>);

  readonly open = signal(false);

  readonly sections = computed<JobSection[]>(() => {
    const s = this.jobs.summary();
    return [
      { title: 'Распознаём документы', icon: 'ph-file-magnifying-glass', group: s.extraction },
      { title: 'Уточняем нормы показателей', icon: 'ph-flask', group: s.labAnalyte },
      { title: 'Обогащаем справочник препаратов', icon: 'ph-pill', group: s.medication },
      { title: 'Проверяем назначенные препараты', icon: 'ph-pill', group: s.visitMedication },
    ].filter((section) => section.group.total > 0);
  });

  get isWide(): boolean {
    return this.breakpoints.tier() === 'wide';
  }

  toggle(): void {
    this.open.update((v) => !v);
  }

  select(item: ActiveJobItem): void {
    this.open.set(false);
    if (item.recordKind === null || item.recordId === null) return;
    void this.router.navigate([relatedKindBasePath(item.recordKind), item.recordId]);
  }

  /** Живая "мысль" модели, если задача реально держит гейт LM Studio прямо сейчас; иначе — её
   * позиция в общей очереди к LLM (план "живой поток мыслей" — не путать пользователя тем, что
   * задача "висит" Running без объяснений, пока справочник насыщается или идёт большой поток
   * анализов); null — вообще ничего показывать не нужно (задача Pending и никого нет впереди).*/
  itemStatusText(item: ActiveJobItem): string | null {
    if (item.waitingForAi) return 'ждём ИИ — начнём автоматически, как только он вернётся';
    if (item.liveText) return item.liveText;
    if (item.queueAhead > 0) {
      return `в очереди — ещё ${item.queueAhead} ${pluralizeRu(item.queueAhead, 'задача', 'задачи', 'задач')} впереди`;
    }
    return null;
  }

  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    if (!this.open() || !this.isWide) return; // мобильная шторка закрывается сама (popstate/Escape/бэкдроп)
    if (!this.host.nativeElement.contains(event.target as Node)) this.open.set(false);
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open() && this.isWide) this.open.set(false);
  }
}
