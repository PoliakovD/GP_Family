/**
 * Тон — пара «мягкий фон + основной цвет» из DESIGN §2.1. Статусы (danger/warning/success) — только по
 * смыслу; accent — действия и навигация; kind-* — виды записей дневника; warm — семья/аватар.
 * CSS-классы `.tone-*` (styles.scss) задают --tone-fg / --tone-bg, которые читают icon-tile, бейджи и строки.
 */
export type Tone =
  | 'neutral' | 'accent' | 'danger' | 'warning' | 'success'
  | 'measure' | 'mood' | 'sleep' | 'note' | 'symptom' | 'medication' | 'warm';
