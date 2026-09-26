import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../../services/api.service';
import { BreakpointService } from '../../services/breakpoint.service';
import { IntakeStateService } from '../../services/intake-state.service';
import { AvatarComponent } from '../../shared/avatar/avatar.component';
import { CourseStatus, CourseSummary, DoseScheduleMode, IntakeSubject } from '../../models/types';
import {
  coversText, progressFraction, progressText, scheduleShort, subjectName, timesText,
} from '../../shared/util/intake-labels';
import { IntakeCourseCardComponent } from '../intake-course-card/intake-course-card.component';

interface CourseGroup {
  subject: IntakeSubject;
  watching: boolean;
  courses: CourseSummary[];
}

/**
 * Вкладка «Курсы» (макет «Screen - Medication schedule»): курсы сгруппированы по людям, справа (десктоп) —
 * карточка выбранного. На узком экране список и карточка — отдельные экраны (`courses` и `courses/:id`).
 * Завершённые курсы — в сворачиваемом архиве.
 */
@Component({
  selector: 'app-intake-courses',
  imports: [AvatarComponent, IntakeCourseCardComponent, RouterLink],
  templateUrl: './intake-courses.component.html',
  styleUrl: './intake-courses.component.scss',
})
export class IntakeCoursesComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly breakpoints = inject(BreakpointService);
  protected readonly intake = inject(IntakeStateService);

  /** Id открытого курса из маршрута `courses/:id`. */
  readonly id = input<string | undefined>(undefined);

  readonly archive = signal<CourseSummary[] | null>(null);
  readonly archiveOpen = signal(false);

  protected readonly isWide = computed(() => this.breakpoints.tier() === 'wide');
  protected readonly Status = CourseStatus;
  protected readonly Mode = DoseScheduleMode;
  protected readonly subjectName = subjectName;

  protected readonly groups = computed<CourseGroup[]>(() => {
    const list = this.intake.courses() ?? [];
    const byPerson = new Map<string, CourseGroup>();
    for (const course of list) {
      const s = course.subject;
      if (!byPerson.has(s.id)) byPerson.set(s.id, { subject: s, watching: false, courses: [] });
      const group = byPerson.get(s.id)!;
      group.courses.push(course);
      group.watching = group.watching || course.isWatching;
    }
    return [...byPerson.values()].sort((a, b) => Number(b.subject.isSelf) - Number(a.subject.isSelf) || a.subject.name.localeCompare(b.subject.name));
  });

  /** На узком экране при открытом курсе список не показываем — только карточку. */
  protected readonly showList = computed(() => this.isWide() || !this.id());
  protected readonly showCard = computed(() => !!this.id());

  constructor() {
    // Десктоп: пока курс не выбран, открываем первый — карточка справа не должна пустовать.
    effect(() => {
      const list = this.intake.courses();
      if (!this.isWide() || this.id() || !list || list.length === 0) return;
      untracked(() => void this.router.navigate(['/health/intake/courses', list[0].id], { replaceUrl: true }));
    });

    effect(() => {
      this.intake.version();
      untracked(() => {
        if (this.archiveOpen()) void this.loadArchive();
      });
    });
  }

  protected person(s: IntakeSubject): { key: string; firstName: string } {
    return { key: s.id, firstName: s.name };
  }

  protected newCourse(): void {
    this.intake.openForm();
  }

  protected async toggleArchive(): Promise<void> {
    this.archiveOpen.update((v) => !v);
    if (this.archiveOpen() && this.archive() === null) await this.loadArchive();
  }

  protected back(): void {
    void this.router.navigate(['/health/intake/courses']);
  }

  protected afterRemoved(): void {
    this.back();
  }

  protected detailLine(c: CourseSummary): string {
    if (c.schedule.mode === DoseScheduleMode.AsNeeded) return scheduleShort(c.schedule, c.food);
    const times = c.schedule.mode === DoseScheduleMode.EveryNHours || c.schedule.mode === DoseScheduleMode.TimesPerDay
      ? timesText(c.schedule) : '';
    const head = scheduleShort(c.schedule, c.food);
    // «1 раз в день · 9:00 · постоянно» — как в макете, когда курс без срока.
    return c.endDate === null && times && c.schedule.mode === DoseScheduleMode.TimesPerDay
      ? `${head.split(' · ')[0]} · ${times} · постоянно` : head;
  }

  protected progress(c: CourseSummary): string {
    return progressText(c.dayNumber, c.totalDays);
  }

  protected fraction(c: CourseSummary): number {
    return progressFraction(c.dayNumber, c.totalDays) * 100;
  }

  protected stockBadge(c: CourseSummary): string | null {
    const s = c.stock;
    if (!s || s.daysCovered === null || s.quantityText === null) return null;
    return s.daysCovered <= 7 ? s.quantityText : null;
  }

  protected stockTitle(c: CourseSummary): string {
    return c.stock?.daysCovered != null ? coversText(c.stock.daysCovered) : '';
  }

  private async loadArchive(): Promise<void> {
    try {
      this.archive.set(await this.api.getCourses(true));
    } catch {
      this.archive.set([]);
    }
  }
}
