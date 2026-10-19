import {
  ChangeDetectionStrategy, Component, DestroyRef, ElementRef, TemplateRef, afterNextRender, contentChild, inject, input,
  signal, viewChild, viewChildren,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';

export interface LandingDemoStep {
  title: string;
  text: string;
}

/** Контекст шаблона макета: текущий шаг и «нажатие уже случилось» (состояние после действия). */
export interface LandingDemoContext {
  $implicit: number;
  done: boolean;
}

/** Пауза на состояние «до» (и докрутку экрана макета) → курсор подходит к цели → «нажимает» → макет
 * переходит в состояние после действия → курсор гаснет. */
const SETTLE_MS = 650;
const MOVE_MS = 700;
const PRESS_MS = 350;
const HIDE_MS = 900;
/** Телефон: шаги листаются сами, пока блок на экране. */
const AUTOPLAY_MS = 4200;

/**
 * Демо фичи на лендинге (DESIGN §4 «Лендинг»): слева шаги, справа «телефон» с макетом экрана, где курсор
 * сам выполняет действие шага. Десктоп — сценарий по прокрутке: телефон прилипает, активный шаг — тот, что
 * пересекает середину экрана. Телефон (< 1024) — шаги переключаются сами, пока блок виден; касание шага
 * останавливает автопроигрывание. Макет декоративный (aria-hidden, inert): смысл несут тексты шагов.
 * prefers-reduced-motion — без курсора и анимаций, сразу итоговое состояние шага.
 *
 * Цель нажатия — элемент макета с `data-tap="<номер шага>"`: курсор ставится в его центр.
 */
@Component({
  selector: 'app-landing-demo',
  standalone: true,
  imports: [NgTemplateOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './landing-demo.component.html',
  styleUrl: './landing-demo.component.scss',
  host: { '[class.ld-demo-reverse]': 'reverse()' },
})
export class LandingDemoComponent {
  readonly kicker = input.required<string>();
  readonly heading = input.required<string>();
  readonly lead = input<string>('');
  readonly steps = input.required<readonly LandingDemoStep[]>();
  /** Телефон слева, шаги справа — чтобы демо подряд не выглядели одинаково. */
  readonly reverse = input(false);

  protected readonly mock = contentChild.required<TemplateRef<LandingDemoContext>>('mock');
  private readonly frame = viewChild.required<ElementRef<HTMLElement>>('frame');
  private readonly stepEls = viewChildren<ElementRef<HTMLElement>>('stepEl');

  protected readonly step = signal(0);
  protected readonly done = signal(false);
  /** Координаты курсора в рамке телефона; null — курсор скрыт. */
  protected readonly cursor = signal<{ x: number; y: number } | null>(null);
  protected readonly cursorVisible = signal(false);
  /** Счётчик нажатий — пересоздаёт «волну» нажатия, чтобы анимация играла заново. */
  protected readonly press = signal(0);

  protected readonly headingId = `ld-demo-${++LandingDemoComponent.nextId}`;
  private static nextId = 0;

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);
  private timers: ReturnType<typeof setTimeout>[] = [];
  private autoplay: ReturnType<typeof setInterval> | null = null;
  private autoplayStopped = false;
  private reducedMotion = false;
  private wide = false;

  constructor() {
    afterNextRender(() => {
      this.reducedMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;
      const wideQuery = matchMedia('(min-width: 1024px)');
      this.wide = wideQuery.matches;
      const onWide = (e: MediaQueryListEvent) => { this.wide = e.matches; if (this.wide) this.stopAutoplay(); };
      wideQuery.addEventListener('change', onWide);

      // Десктоп: активный шаг — тот, что пересекает середину экрана.
      const stepObserver = new IntersectionObserver((entries) => {
        if (!this.wide) return;
        for (const e of entries) {
          if (e.isIntersecting) this.go(Number((e.target as HTMLElement).dataset['step']));
        }
      }, { rootMargin: '-50% 0px -50% 0px' });
      for (const el of this.stepEls()) stepObserver.observe(el.nativeElement);

      // Телефон: автопроигрывание, пока блок виден хотя бы наполовину.
      const sectionObserver = new IntersectionObserver(([e]) => {
        if (this.wide) return;
        if (e.isIntersecting) this.startAutoplay(); else this.stopAutoplay();
      }, { threshold: 0.5 });
      sectionObserver.observe(this.host.nativeElement);

      this.play(0);
      this.destroyRef.onDestroy(() => {
        stepObserver.disconnect();
        sectionObserver.disconnect();
        wideQuery.removeEventListener('change', onWide);
        this.clearTimers();
        this.stopAutoplay();
      });
    });
  }

  /** Касание/клик по шагу: на телефоне — сразу показать его и не листать дальше самим. */
  protected select(index: number): void {
    this.autoplayStopped = true;
    this.stopAutoplay();
    if (this.wide) {
      this.stepEls()[index]?.nativeElement.scrollIntoView({ behavior: this.reducedMotion ? 'auto' : 'smooth', block: 'center' });
    } else {
      this.go(index);
    }
  }

  private go(index: number): void {
    if (index === this.step()) return;
    this.play(index);
  }

  /** Сценарий шага: состояние «до» → курсор к цели → нажатие → состояние «после». */
  private play(index: number): void {
    this.clearTimers();
    this.step.set(index);
    if (this.reducedMotion) {
      this.done.set(true);
      this.cursor.set(null);
      return;
    }
    this.done.set(false);
    // Цель ищем после отрисовки состояния «до» этого шага.
    this.timers.push(setTimeout(() => {
      const target = this.frame().nativeElement.querySelector<HTMLElement>(`[data-tap~="${index}"]`);
      if (!target) { this.done.set(true); return; }
      const f = this.frame().nativeElement.getBoundingClientRect();
      const t = target.getBoundingClientRect();
      this.cursor.set({ x: t.left - f.left + t.width / 2, y: t.top - f.top + t.height / 2 });
      this.cursorVisible.set(true);
      this.timers.push(setTimeout(() => {
        this.press.update((n) => n + 1);
        this.timers.push(setTimeout(() => {
          this.done.set(true);
          this.timers.push(setTimeout(() => this.cursorVisible.set(false), HIDE_MS));
        }, PRESS_MS));
      }, MOVE_MS));
    }, SETTLE_MS));
  }

  private startAutoplay(): void {
    if (this.autoplay || this.autoplayStopped) return;
    this.autoplay = setInterval(() => this.play((this.step() + 1) % this.steps().length), AUTOPLAY_MS);
  }

  private stopAutoplay(): void {
    if (this.autoplay) clearInterval(this.autoplay);
    this.autoplay = null;
  }

  private clearTimers(): void {
    for (const t of this.timers) clearTimeout(t);
    this.timers = [];
  }
}
