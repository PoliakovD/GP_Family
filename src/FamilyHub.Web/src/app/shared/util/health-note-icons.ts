import {
  Activity, Angry, Frown, Laugh, Meh, Moon, PenLine, Pill, Smile, Zap, type LucideIconData,
} from 'lucide-angular';
import { HealthNoteKind } from '../../models/types';
import type { Tone } from '../ui/tone';

/** Иконка (Lucide) и тон вида записи дневника (DESIGN §2.1 kind-*): плитка ленты и выбор вида в форме. */
export const HEALTH_KIND_UI: Record<HealthNoteKind, { icon: LucideIconData; tone: Tone }> = {
  [HealthNoteKind.Symptom]: { icon: Zap, tone: 'symptom' },
  [HealthNoteKind.Metric]: { icon: Activity, tone: 'measure' },
  [HealthNoteKind.Wellbeing]: { icon: Smile, tone: 'mood' },
  [HealthNoteKind.MedicationIntake]: { icon: Pill, tone: 'medication' },
  [HealthNoteKind.Sleep]: { icon: Moon, tone: 'sleep' },
  [HealthNoteKind.Note]: { icon: PenLine, tone: 'note' },
};

/** Самочувствие 1–5 → лицо (ужасно … отлично). */
export const WELLBEING_ICON: Record<number, LucideIconData> = { 1: Angry, 2: Frown, 3: Meh, 4: Smile, 5: Laugh };
