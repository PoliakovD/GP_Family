import { AfterViewInit, Component, ElementRef, EventEmitter, OnDestroy, Output, inject } from '@angular/core';

/**
 * Невидимый маркер конца списка (редизайн v2, «Анализы» — бесконечная прокрутка вместо
 * нумерованных страниц, см. план редизайна PR3b). IntersectionObserver — на СОБСТВЕННОМ
 * host-элементе компонента, поэтому таймингом ngAfterViewInit/ngOnDestroy управляет сам Angular
 * при вставке/удалении из родительского `@if` — не нужен хрупкий `@ViewChild` поверх условного
 * рендера (родитель показывает/прячет этот компонент целиком через `@if (hasMore())`).
 */
@Component({
  selector: 'app-infinite-scroll-sentinel',
  standalone: true,
  template: '',
  host: { style: 'display:block;height:1px' },
})
export class InfiniteScrollSentinelComponent implements AfterViewInit, OnDestroy {
  @Output() readonly visible = new EventEmitter<void>();

  private readonly host = inject(ElementRef<HTMLElement>);
  private observer?: IntersectionObserver;

  ngAfterViewInit(): void {
    this.observer = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting)) this.visible.emit();
    });
    this.observer.observe(this.host.nativeElement);
  }

  ngOnDestroy(): void {
    this.observer?.disconnect();
  }
}
