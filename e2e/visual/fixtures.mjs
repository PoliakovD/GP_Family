// Фикстуры API для visual/shots.mjs. Данные повторяют макеты docs/redesign/screens/png/:
// семья «Поляковы» (Даниил — админ, Валерия — участник), просроченное лекарство, анализ крови
// с 6 показателями вне нормы, прививка от гриппа, дни рождения. «Сегодня» — 14.10.2026 (shots.mjs).
//
// Неизвестный запрос → undefined → shots.mjs отвечает 404 и печатает его в конце прогона:
// так видно, какую фикстуру добавить для нового экрана.

import { ADMIN_SCREENS, ADMIN_ROUTES } from './fixtures-admin.mjs';

const FAMILY_ID = 'f-polyakov';
const ME_ID = 'u-daniil';
const VALERIA_ID = 'u-valeria';

/** Экраны: name — имя файла, path — маршрут, only — только 'desktop' | 'mobile',
 *  spinner — на экране есть постоянный спиннер (не ждать, пока исчезнет). */
export const SCREENS = [
  { name: 'home', path: '/home' },
  { name: 'health', path: '/health' },
  { name: 'family', path: `/families/${FAMILY_ID}` },
  { name: 'family-members', path: `/families/${FAMILY_ID}?tab=members` },
  { name: 'family-medkits', path: `/families/${FAMILY_ID}?tab=medkits`, only: 'desktop' },
  { name: 'family-birthdays', path: `/families/${FAMILY_ID}?tab=birthdays`, only: 'desktop' },
  { name: 'family-dependents', path: `/families/${FAMILY_ID}?tab=dependents`, only: 'mobile' },
  { name: 'record', path: '/health/records/r-blood' },
  { name: 'record-open', path: '/health/records/r-blood', only: 'desktop', prepare: async (page) => {
    await page.getByRole('button', { name: /вне нормы/ }).click();
    await page.getByRole('button', { name: 'Резюме для врача' }).click();
    await page.getByRole('button', { name: 'Бланк', exact: true }).click();
    await page.waitForTimeout(300);
  } },
  { name: 'indicator', path: '/health/records/r-blood?indicator=wbc', only: 'mobile' },
  { name: 'indicator-panel', path: '/health/records/r-blood', only: 'desktop', prepare: async (page) => {
    await page.getByRole('cell', { name: /Лейкоциты/ }).click();
    await page.waitForTimeout(400);
  } },
  { name: 'record-add', path: '/health/records/new' },
  { name: 'records-batch', path: '/health/records/batch' },
  { name: 'records', path: '/health/records' },
  { name: 'visits', path: '/health/visits' },
  { name: 'visit', path: '/health/visits/v-1' },
  { name: 'indicators', path: '/health/indicators' },
  { name: 'kb', path: '/health/kb/medications' },
  { name: 'kb-indicators', path: '/health/kb/indicators', only: 'desktop' },
  { name: 'kb-vaccines', path: '/health/kb/vaccines' },
  { name: 'intake', path: '/health/intake' },
  { name: 'intake-form', path: '/health/intake', prepare: async (page) => {
    await page.getByRole('button', { name: 'Новый курс' }).click();
    await page.getByRole('radio', { name: /Вручную/ }).click();
    await page.waitForTimeout(400);
  } },
  { name: 'intake-course', path: '/health/intake/courses/c-1', only: 'mobile' },
  { name: 'vaccinations', path: '/health/vaccinations' },
  { name: 'vaccination-add', path: '/health/vaccinations', only: 'desktop', prepare: async (page) => {
    await page.getByRole('button', { name: 'Добавить прививку' }).click();
    await page.getByRole('button', { name: /Отметить по календарю/ }).click();
    await page.waitForTimeout(400);
  } },
  { name: 'vaccination-person', path: `/health/vaccinations/people/user/${ME_ID}/series/influenza`, only: 'mobile' },
  { name: 'notes', path: '/health/notes' },
  { name: 'notes-form', path: '/health/notes', prepare: async (page) => {
    await page.getByRole('button', { name: 'Добавить запись' }).first().click();
    await page.waitForTimeout(500);
  } },
  { name: 'notes-form-metric', path: '/health/notes', only: 'desktop', prepare: async (page) => {
    await page.getByRole('button', { name: 'Добавить запись' }).first().click();
    await page.getByRole('radio', { name: 'Замер' }).click();
    await page.waitForTimeout(400);
  } },
  { name: 'doctor-reports', path: '/health/reports' },
  { name: 'medkits', path: '/health/medications' },
  { name: 'medkit', path: '/health/medications/mk1' },
  // Витрина компонентов (только dev-сборка, фаза 2 редизайна).
  { name: 'settings', path: '/settings' },
  { name: 'settings-profile', path: '/settings/profile' },
  { name: 'settings-security', path: '/settings/security', only: 'desktop' },
  { name: 'settings-notifications', path: '/settings/notifications' },
  { name: 'settings-data', path: '/settings/data' },
  // Уведомления, поиск, фоновые задачи (4.8.2).
  { name: 'notifications', path: '/notifications' },
  { name: 'search', path: '/home', only: 'desktop', prepare: async (page) => {
    await page.locator('.app-search input').fill('Ибу');
    await page.waitForTimeout(600);
  } },
  { name: 'jobs', path: '/home', only: 'desktop', overrides: () => [['GET', /^\/api\/jobs\/active-summary$/, ACTIVE_JOBS]],
    prepare: async (page) => { await page.getByRole('button', { name: 'Фоновые процессы' }).click(); } },
  // Публичный отчёт для врача (4.8.3) — без сессии.
  { name: 'public-report', path: '/r/visual-token', anon: true },
  { name: 'public-report-gone', path: '/r/gone-token', anon: true, only: 'mobile' },
  // Вход и онбординг (4.8.1). anon — /api/auth/me отвечает 401, как у человека без сессии.
  { name: 'auth-login', path: '/login', anon: true },
  { name: 'auth-register', path: '/login', anon: true, prepare: async (page) => {
    await page.getByRole('button', { name: 'Создать аккаунт' }).click();
  } },
  { name: 'auth-reset', path: '/login', anon: true, only: 'mobile', prepare: async (page) => {
    await page.getByRole('button', { name: 'Забыли пароль?' }).click();
  } },
  { name: 'auth-join', path: '/join/visual-code', anon: true },
  { name: 'auth-join-bad', path: '/join/bad-code', anon: true, only: 'mobile' },
  { name: 'auth-telegram-bind', path: '/telegram-bind', anon: true, only: 'mobile' },
  { name: 'auth-telegram-expired', path: '/telegram-expired', anon: true, only: 'mobile' },
  { name: 'auth-profile-setup', path: '/profile-setup' },
  { name: 'auth-consent', path: '/consent' },
  { name: 'auth-consent-text', path: '/consent-text', anon: true },
  { name: 'auth-privacy', path: '/privacy', anon: true },
  { name: 'auth-not-found', path: '/no-such-page' },
  { name: 'dev-ui', path: '/dev/ui', spinner: true }, // спиннер — часть витрины
  ...ADMIN_SCREENS,
];

const ME = {
  userId: ME_ID, lastName: 'Поляков', firstName: 'Даниил', middleName: null, birthDate: '1995-08-25',
  gender: 0, profileComplete: true, provider: 'email', email: 'daniil@example.com', username: 'daniil',
  tgUsername: null, hasTelegram: false, hasPassword: true, timeZoneId: 'Europe/Moscow',
};

const MEMBERS = [
  { id: VALERIA_ID, lastName: 'Полякова', firstName: 'Валерия', middleName: null, username: 'valeria', role: 0, joinedAt: '2026-09-13T10:00:00Z' },
  { id: ME_ID, lastName: 'Поляков', firstName: 'Даниил', middleName: null, username: 'daniil', role: 1, joinedAt: '2026-08-25T10:00:00Z' },
];

const FAMILIES = [
  { id: FAMILY_ID, name: 'Поляковы', myRole: 1, myStatus: 1, currentMembers: MEMBERS, dependents: [] },
];

const emptyJobs = { total: 0, items: [] };

const HOME_SUMMARY = {
  greetingName: 'Даниил',
  today: '2026-10-14',
  attentionTotal: 3,
  primaryFamilyId: FAMILY_ID,
  primaryFamilyName: 'Поляковы',
  medications: [
    { medicationId: 'm1', medkitId: 'mk1', medkitName: 'Тестовая', familyId: FAMILY_ID, familyName: 'Поляковы',
      name: 'Суматриптан', expiryDate: '2023-02-01', daysLeft: -1351, severity: 'expired' },
    { medicationId: 'm2', medkitId: 'mk1', medkitName: 'Тестовая', familyId: FAMILY_ID, familyName: 'Поляковы',
      name: 'Нурофен', expiryDate: '2026-11-01', daysLeft: 18, severity: 'expiring' },
  ],
  joinRequests: [],
  birthdays: [
    { familyId: FAMILY_ID, familyName: 'Поляковы', personName: 'Мама Дани (Алёна)', date: '2026-11-24', daysUntil: 41, turningAge: 49, source: 0 },
    { familyId: FAMILY_ID, familyName: 'Поляковы', personName: 'Папа Дани (Олег)', date: '2027-01-03', daysUntil: 81, turningAge: 52, source: 0 },
    { familyId: FAMILY_ID, familyName: 'Поляковы', personName: 'Мама Лерочки (Инна)', date: '2027-01-22', daysUntil: 100, turningAge: 52, source: 0 },
  ],
  ok: {
    medicationsInDate: 5, medicationsTotal: 7, medicationsExpired: 1, medicationsExpiring: 1,
    analysesTotal: 4, analysesAbnormal: 6, visitsTotal: 3, visitsLastDate: '2026-06-16', pushEnabled: true,
  },
  unreadNotifications: 2,
};

const HEALTH_SUMMARY = {
  intake: {
    taken: 1, missed: 0, upcoming: 2, skipped: 0, total: 3,
    next: { courseId: 'c-1', scheduledAt: '2026-10-14T09:00:00+03:00', localTime: '09:00', drugName: 'Витамин D3', units: 1, unit: 1, canAct: true },
  },
  diary: {
    latestBloodPressure: { value: 122, value2: 95, occurredAt: '2026-10-03T23:51:00+03:00', recentValues: [118, 121, 116, 124, 120, 122] },
    latestSymptom: { title: 'Головная боль', severity: 4, occurredAt: '2026-10-02T10:00:00+03:00' },
  },
  analyses: {
    latest: { id: 'r-blood', title: 'Развернутый анализ крови', recordDate: '2026-10-03', doctor: null, indicatorCount: 20, abnormalIndicatorCount: 6, prescriptionCount: null },
    total: 4,
  },
  visits: {
    latest: { id: 'v-1', title: 'Терапевт', recordDate: '2026-06-16', doctor: 'Иванова А. С.', indicatorCount: 0, abnormalIndicatorCount: 0, prescriptionCount: 2 },
    total: 3,
  },
  indicators: {
    trackedCount: 12,
    highlight: { analyteKey: 'wbc', displayName: 'Лейкоциты', valueRaw: '4,24', unit: '×10⁹/л', flag: 1, recentValues: [6.1, 5.4, 4.9, 4.24], firstDate: '2025-04-01', lastDate: '2026-10-03' },
  },
  vaccinations: {
    nextDue: { seriesName: 'Грипп', status: 3, windowFrom: '2026-09-01', windowTo: '2026-11-30' },
    lastDone: { label: 'Столбняк, дифтерия', date: '2021-05-10' },
  },
  medkits: { expiring: 1, expired: 1 },
  reports: { activeLinks: 1 },
};

const SELF_SUBJECT = { kind: 'user', id: ME_ID, name: 'Даниил Поляков', isSelf: true };
const VALERIA_SUBJECT = { kind: 'user', id: VALERIA_ID, name: 'Валерия Полякова', isSelf: false };

const dose = (courseId, time, drugName, outcome, extra = {}) => ({
  courseId, doseId: `${courseId}-${time}`, scheduledAt: `2026-10-14T${time}:00+03:00`, localTime: time,
  period: time < '12:00' ? 0 : time < '18:00' ? 1 : 2, drugName, units: 1, unit: 0, food: 2,
  subject: SELF_SUBJECT, outcome, takenAt: outcome === 2 ? `2026-10-14T${time}:05+03:00` : null,
  snoozedUntil: null, dayNumber: 5, totalDays: 30, canAct: outcome === 1 || outcome === 0, isWatching: false, ...extra,
});

const INTAKE_TODAY = {
  date: '2026-10-14', timeZoneId: 'Europe/Moscow',
  counters: { taken: 1, missed: 0, upcoming: 2, skipped: 0, total: 3, nextAt: '2026-10-14T09:00:00+03:00' },
  subjects: [SELF_SUBJECT, VALERIA_SUBJECT],
  items: [
    dose('c-2', '08:00', 'Омепразол', 2),
    dose('c-1', '09:00', 'Витамин D3', 1),
    dose('c-3', '21:00', 'Магний B6', 0),
  ],
  asNeeded: [],
  alerts: [],
  lowStock: [{ courseId: 'c-3', drugName: 'Магний B6', quantityText: '4 таблетки', daysCovered: 4, lowStockDays: 5, courseDaysLeft: 25 }],
  week: ['08', '09', '10', '11', '12', '13', '14'].map((d) => ({
    date: `2026-10-${d}`, onTime: d === '14' ? 1 : 3, late: d === '11' ? 1 : 0, missed: 0, skipped: 0,
    upcoming: d === '14' ? 2 : 0, isToday: d === '14',
  })),
  weekOnTimePercent: 95,
};

const COURSE = {
  id: 'c-1', drugName: 'Витамин D3', subject: SELF_SUBJECT, status: 0,
  schedule: { mode: 0, times: [{ at: '09:00', units: 1 }] }, food: 2, unit: 1,
  startDate: '2026-10-10', endDate: '2026-11-08', dayNumber: 5, totalDays: 30,
  canEdit: true, isWatching: false, missedToday: false, writeOffEnabled: false, stock: null,
};

const vaccItem = (seriesCode, seriesName, label, status, extra = {}) => ({
  seriesCode, doseIndex: 0, label, seriesName, seriesShortName: seriesName, group: 1, stage: 'Взрослые',
  status, windowFrom: null, windowTo: null, recordId: null, date: null, isRepeat: false, ...extra,
});
const ME_VACC = { ...SELF_SUBJECT, canEdit: true };
const VALERIA_VACC = { ...VALERIA_SUBJECT, canEdit: false };

const VACC_OVERVIEW = {
  attention: [
    { subject: ME_VACC, item: vaccItem('influenza', 'Грипп', 'Сезон 2026/27', 3, { windowFrom: '2026-09-01', windowTo: '2026-11-30', isRepeat: true }) },
    { subject: ME_VACC, item: vaccItem('td', 'Дифтерия, столбняк', 'Ревакцинация', 2, { windowFrom: '2026-10-01', windowTo: '2027-05-10' }) },
  ],
  people: [
    { subject: ME_VACC, done: 6, dueSoon: 1, canDo: 1, noData: 3, total: 11, hint: null },
    { subject: VALERIA_VACC, done: 8, dueSoon: 0, canDo: 0, noData: 2, total: 10, hint: 'Нет даты рождения у части записей' },
  ],
};

const VACC_PERSON = {
  subject: ME_VACC, ageYears: 31, ageMonths: 1, hasBirthDate: true,
  custom: [],
  byDisease: [
    { disease: 'Грипп', items: [vaccItem('influenza', 'Грипп', 'Сезон 2026/27', 3, { windowFrom: '2026-09-01', windowTo: '2026-11-30', isRepeat: true })] },
    { disease: 'Дифтерия, столбняк', items: [
      vaccItem('td', 'Дифтерия, столбняк', 'V1', 0, { date: '2011-05-10', recordId: 'vr-1' }),
      vaccItem('td', 'Дифтерия, столбняк', 'Ревакцинация', 2, { doseIndex: 1, windowFrom: '2026-10-01', windowTo: '2027-05-10' }),
    ] },
    { disease: 'Корь', items: [vaccItem('measles', 'Корь', 'V1', 5)] },
  ],
};
VACC_PERSON.byAge = VACC_PERSON.byDisease.flatMap((g) => g.items);

const VACC_SERIES = {
  seriesCode: 'influenza', seriesName: 'Грипп', diseases: ['Грипп'], group: 2,
  about: 'Ежегодная прививка перед сезоном гриппа.', contraindications: null, tradeNames: ['Совигрипп', 'Ультрикс'],
  reactionHint: 'Возможны слабость и температура до 37,5 в течение суток.',
  doses: [
    { doseIndex: 0, label: 'Сезон 2025/26', status: 0, windowFrom: null, windowTo: null, recordId: 'vr-2', date: '2025-10-20', vaccineName: 'Совигрипп', files: [], diaryEntries: [] },
    { doseIndex: 1, label: 'Сезон 2026/27', status: 3, windowFrom: '2026-09-01', windowTo: '2026-11-30', recordId: null, date: null, vaccineName: null, files: [], diaryEntries: [] },
  ],
};

const note = (id, kind, occurredAt, extra) => ({
  id, kind, occurredAt, title: null, text: null, includeInDoctorQuestions: false,
  symptom: null, metric: null, wellbeing: null, intake: null, sleep: null, updatedAt: occurredAt, ...extra,
});
const HEALTH_NOTES = [
  note('n1', 5, '2026-10-04T16:46:00+03:00', { title: 'Тестовая', text: 'Заметка с вложением' }),
  note('n2', 1, '2026-10-03T23:51:00+03:00', { metric: { code: 'pulse', value: 80 } }),
  note('n3', 1, '2026-10-03T23:51:00+03:00', { metric: { code: 'blood_pressure', value: 122, value2: 95 } }),
  note('n4', 2, '2026-10-03T23:50:00+03:00', { wellbeing: { score: 4 } }),
  note('n5', 4, '2026-10-03T09:00:00+03:00', { sleep: { bedTime: '2026-10-03T01:30:00+03:00', wakeTime: '2026-10-03T09:00:00+03:00', quality: 2 } }),
  note('n6', 4, '2026-10-02T10:00:00+03:00', { sleep: { bedTime: '2026-10-02T01:00:00+03:00', wakeTime: '2026-10-02T10:00:00+03:00', quality: 1 } }),
  note('n7', 2, '2026-10-02T01:17:00+03:00', { wellbeing: { score: 3 } }),
  note('n8', 0, '2026-10-01T18:20:00+03:00', { title: 'Головная боль', symptom: { severity: 4, areas: ['Голова'] } }),
];
const HEALTH_NOTE_CATALOG = {
  metrics: [
    { code: 'blood_pressure', name: 'Давление', unit: 'мм рт. ст.', min: 50, max: 250, hasSecondValue: true, min2: 30, max2: 150 },
    { code: 'pulse', name: 'Пульс', unit: 'уд/мин', min: 30, max: 220, hasSecondValue: false, min2: null, max2: null },
    { code: 'weight', name: 'Вес', unit: 'кг', min: 1, max: 300, hasSecondValue: false, min2: null, max2: null },
  ],
  bodyAreas: ['Голова', 'Живот', 'Спина'],
  wellbeingFactors: ['Сон', 'Стресс'],
};

const DOCTOR_REPORTS = [{
  id: 'dr-1', periodFrom: '2026-07-01', periodTo: '2026-10-01', createdAt: '2026-10-02T12:00:00Z', pageCount: 4, blockCount: 5,
  recipient: 'Терапевт', blocks: { labs: true, aiSummaries: true, medications: true, visits: true, measurements: true, symptomsNotes: false, vaccinations: false },
  link: { status: 1, token: 'tok', expiresAt: '2026-10-20T12:00:00Z', revokedAt: null, viewCount: 2, lastViewedAt: '2026-10-05T09:00:00Z' },
  subjectKind: 0, subjectId: null, subjectName: null, subjectIsPet: false, createdByName: 'Даниил Поляков', canManage: true,
}];

const VALERIA_AUTHOR = { userId: VALERIA_ID, lastName: 'Полякова', firstName: 'Валерия', middleName: null };
const DANIIL_AUTHOR = { userId: ME_ID, lastName: 'Поляков', firstName: 'Даниил', middleName: null };
const chatMsg = (id, author, text, createdAt, extra = {}) => ({
  id, familyId: FAMILY_ID, author, text, createdAt, editedAt: null, deleted: false, replyTo: null,
  attachments: [], reactions: [], links: [], canEdit: author === DANIIL_AUTHOR, canDelete: author === DANIIL_AUTHOR, ...extra,
});
// Новые — первыми (как отдаёт API, компонент разворачивает).
const CHAT_PAGE = {
  items: [
    chatMsg('m3', VALERIA_AUTHOR, 'Покажи врачу лейкоциты, они заметно упали.', '2026-10-14T06:31:00Z'),
    chatMsg('m2', DANIIL_AUTHOR, 'Ещё нет, на этой неделе запишусь. Скинул свежий анализ:', '2026-10-14T06:20:00Z', {
      links: [{ kind: 0, entityId: 'r-blood', accessible: true, title: 'Развернутый анализ крови', subtitle: '3 октября · 6 вне нормы', routeKind: 0 }],
    }),
    chatMsg('m1', VALERIA_AUTHOR, 'Даня, ты записался на прививку от гриппа? Нужно до конца ноября.', '2026-10-14T06:12:00Z'),
  ],
  nextCursor: null,
};

// Анализ крови из макета analysis-desktop: 6 вне нормы, 13 в норме, 1 без нормы в бланке (гемоглобин —
// DESIGN > макет: «норма не указана» → без нормы, не «в норме»).
const IND = (id, pos, name, raw, unit, low, high, flag, extra = {}) => ({
  id, analyteKey: id, displayName: name, flag, refSource: low === null ? 0 : 1, specimenKbId: 'blood',
  specimenDisplayName: 'Кровь', position: pos, valueRaw: raw, unit, refLowText: low, refHighText: high,
  refText: null, recordDate: '2026-10-03', medicalRecordId: 'r-blood', valueNumericText: raw.replace(',', '.'),
  kbAnalyteId: id, rawDisplayName: null, enrichmentPending: false, enrichmentQueueAhead: 0,
  enrichmentWaitingForAi: false, panelLabel: null, ...extra,
});
const INDICATORS = [
  IND('esr', 1, 'СОЭ', '6', 'мм/ч', '0', '2', 3, { rawDisplayName: 'СОЭ по Панченкову' }),
  IND('wbc', 2, 'Лейкоциты', '4,24', '×10⁹/л', '6.5', '12.5', 1),
  IND('rbc', 3, 'Эритроциты', '5,49', '×10¹²/л', '3.3', '4.9', 3),
  IND('neut', 4, 'Нейтрофилы', '2,28', '×10⁹/л', '40.8', '70.39', 1),
  IND('lymph', 5, 'Лимфоциты', '40', '%', '17', '38', 3),
  IND('eos', 6, 'Эозинофилы', '1', '%', '0.04', '0.44', 3),
  IND('hgb', 7, 'Гемоглобин', '164', 'г/л', null, null, 0),
  IND('plt', 8, 'Тромбоциты', '245', '×10⁹/л', '150', '400', 2),
  IND('hct', 9, 'Гематокрит', '44', '%', '40', '48', 2),
  IND('mcv', 10, 'Средний объём эритроцита', '88', 'фл', '80', '100', 2),
  IND('mch', 11, 'Среднее содержание Hb', '30', 'пг', '27', '34', 2),
  IND('mchc', 12, 'Средняя концентрация Hb', '340', 'г/л', '300', '380', 2),
  IND('rdw', 13, 'Ширина распределения эритроцитов', '12,8', '%', '11.5', '14.5', 2),
  IND('mono', 14, 'Моноциты', '7', '%', '3', '11', 2),
  IND('baso', 15, 'Базофилы', '0,5', '%', '0', '1', 2),
  IND('mpv', 16, 'Средний объём тромбоцита', '9,8', 'фл', '7.4', '10.4', 2),
  IND('pct', 17, 'Тромбокрит', '0,24', '%', '0.15', '0.4', 2),
  IND('pdw', 18, 'Ширина распределения тромбоцитов', '13', '%', '10', '17', 2),
  IND('ret', 19, 'Ретикулоциты', '1,1', '%', '0.5', '1.5', 2),
  IND('band', 20, 'Палочкоядерные', '3', '%', '1', '6', 2),
];
const RECORD = (id, title, date, extra = {}) => ({
  id, ownerUserId: ME_ID, kind: 0, personName: 'Поляков Даниил', recordDate: date, doctor: null, title,
  description: null, extractionStatus: 2, createdAt: `${date}T09:00:00Z`, hiddenFamilyIds: [],
  familyDependentId: null, targetUserId: null, attachmentCount: 1, unrecognizedAttachmentCount: 0,
  indicatorCount: 20, abnormalIndicatorCount: 6, normalIndicatorCount: 13, specimenKbId: 'blood',
  specimenDisplayName: 'Кровь', specimenHint: null, kindIsAutoDetected: false, pendingSpecimenText: null, ...extra,
});
const R_BLOOD = RECORD('r-blood', 'Развернутый анализ крови', '2026-10-03');
const RECORDS = [
  R_BLOOD,
  RECORD('r-bio', 'Биохимия крови', '2026-08-12', { indicatorCount: 9, abnormalIndicatorCount: 1, normalIndicatorCount: 8 }),
  RECORD('r-urine', 'Общий анализ мочи', '2026-06-20', { indicatorCount: 11, abnormalIndicatorCount: 0, normalIndicatorCount: 11,
    specimenKbId: 'urine', specimenDisplayName: 'Моча' }),
];
const VISITS = [
  RECORD('v-1', 'Терапевт', '2026-09-18', { kind: 1, doctor: 'Иванова Мария Петровна', indicatorCount: 0,
    abnormalIndicatorCount: 0, normalIndicatorCount: 0, specimenKbId: '', specimenDisplayName: null }),
];

// Минимальный одностраничный PDF (A4) для просмотрщика публичного отчёта: латиница Helvetica + полосы-«таблица».
function samplePdf() {
  const lines = ['BT /F1 20 Tf 56 770 Td (Doctor report) Tj ET', 'BT /F1 11 Tf 56 748 Td (Polyakov D. S. - 01.04.2026 - 14.10.2026) Tj ET'];
  for (let i = 0; i < 14; i++) {
    const y = 700 - i * 34;
    lines.push(`0.94 0.94 0.92 rg 56 ${y} 483 26 re f`, `0.2 0.2 0.2 rg BT /F1 10 Tf 64 ${y + 9} Td (Indicator ${i + 1}) Tj ET`);
  }
  const stream = lines.join('\n');
  const objs = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
    `<< /Length ${stream.length} >>\nstream\n${stream}\nendstream`, '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'];
  let pdf = '%PDF-1.4\n';
  const offsets = objs.map((o, i) => { const at = pdf.length; pdf += `${i + 1} 0 obj\n${o}\nendobj\n`; return at; });
  const xref = pdf.length;
  pdf += `xref\n0 ${objs.length + 1}\n0000000000 65535 f \n` + offsets.map((o) => `${String(o).padStart(10, '0')} 00000 n \n`).join('');
  pdf += `trailer\n<< /Size ${objs.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF`;
  return pdf;
}

const job = (jobId, label, extra = {}) => ({ jobId, label, recordId: null, recordKind: null, createdAt: '2026-10-14T09:30:00+03:00',
  isRunning: false, waitingForAi: false, queueAhead: 0, ...extra });
const ACTIVE_JOBS = {
  extraction: { total: 2, items: [job('j1', 'Анализ мочи — 12 окт.', { recordId: 'r-blood', recordKind: 0, isRunning: true }), job('j2', 'Бланк УЗИ', { queueAhead: 1 })] },
  labAnalyte: { total: 1, items: [job('j3', 'Ферритин')] },
  medication: emptyJobs, visitMedication: emptyJobs,
};
const NOTE = (id, type, title, body, createdAt, isRead, kind = null, related = 'r-blood') => ({
  id, type, title, body, createdAt, isRead, readAt: null, relatedEntityKind: kind, relatedEntityId: related });
const NOTIFICATIONS = [
  NOTE('n1', 11, 'Пропущен приём: Аторвастатин', 'Вечерний приём в 21:00 не отмечен.', '2026-10-14T08:10:00+03:00', false, 3, 'dose-1'),
  NOTE('n2', 7, 'Анализ крови распознан', 'Развернутый анализ крови — 20 показателей, 6 вне нормы.', '2026-10-14T07:45:00+03:00', false, 0),
  NOTE('n3', 17, 'Валерия: «Купила витамин D»', 'Сообщение в чате семьи «Поляковы».', '2026-10-13T19:20:00+03:00', true, 8, 'fam'),
  NOTE('n4', 0, 'Ибупрофен: срок годности истекает', 'Аптечка «Дом» — годен до 31 октября.', '2026-10-13T09:00:00+03:00', true),
  NOTE('n5', 13, 'Скоро прививка: грипп', 'Ежегодная вакцинация — рекомендуем до конца октября.', '2026-10-10T09:00:00+03:00', true),
];
const SEARCH = [
  { type: 0, id: 's1', title: 'Ибупрофен 200 мг', snippet: null, score: 1, birthday: null,
    medication: { familyId: 'fam', familyName: 'Поляковы', medkitId: 'mk', medkitName: 'Дом', expiryDate: '2026-10-31' } },
  { type: 1, id: 's2', title: 'Ибупрофен', snippet: 'НПВС: обезболивающее, жаропонижающее', score: 0.8, birthday: null, medication: null },
  { type: 2, id: 's3', title: 'Развернутый анализ крови', snippet: '3 октября · 6 вне нормы', score: 0.5, birthday: null, medication: null },
];

/** [метод, регулярка пути, ответ | (match, query, body) => ответ] */
const ROUTES = [
  ...ADMIN_ROUTES,
  ['GET', /^\/api\/auth\/me$/, ME],
  ['GET', /^\/api\/consents\/status$/, { accepted: true, version: '1' }],
  ['GET', /^\/api\/consents\/current$/, { version: '2026-03', text: '<h2>Согласие на обработку персональных данных</h2><p>Я, субъект персональных данных, даю согласие ООО «Фэмили Хаб» на обработку моих персональных данных: фамилии, имени, отчества, даты рождения, пола, адреса электронной почты.</p><h3>Цели обработки</h3><ul><li>предоставление доступа к сервису;</li><li>напоминания о приёме лекарств и прививках;</li><li>хранение медицинских записей семьи.</li></ul><p>Согласие действует до его отзыва в разделе «Данные и доступ».</p>' }],
  ['GET', /^\/api\/legal\/privacy-policy$/, { rawText: '<h2>Согласие на обработку персональных данных</h2><p>Я, субъект персональных данных, даю согласие ООО «Фэмили Хаб» на обработку моих персональных данных: фамилии, имени, отчества, даты рождения, пола, адреса электронной почты.</p><h3>Цели обработки</h3><ul><li>предоставление доступа к сервису;</li><li>напоминания о приёме лекарств и прививках;</li><li>хранение медицинских записей семьи.</li></ul><p>Согласие действует до его отзыва в разделе «Данные и доступ».</p>'.replace('Согласие на обработку персональных данных', 'Политика конфиденциальности') }],
  ['GET', /^\/api\/public\/doctor-reports\/visual-token$/, { patientName: 'Поляков Даниил Сергеевич', sex: 'М', age: 31,
    birthDate: '1995-08-25', periodFrom: '2026-04-01', periodTo: '2026-10-14', createdAt: '2026-10-14T09:00:00+03:00',
    expiresAt: '2026-10-21T09:00:00+03:00', pageCount: 4, isPet: false,
    sections: ['Анализы вне нормы', 'Динамика показателей', 'Приём лекарств', 'Дневник самочувствия'] }],
  ['GET', /^\/api\/public\/doctor-reports\/visual-token\/pdf$/, () => ({ rawText: samplePdf(), contentType: 'application/pdf' })],
  ['GET', /^\/api\/public\/doctor-reports\/gone-token$/, { status: 404, body: { code: 'not_found' } }],
  ['GET', /^\/api\/invites\/visual-code\/preview$/, { familyName: 'Поляковы', inviterName: 'Валерия Полякова' }],
  ['GET', /^\/api\/invites\/bad-code\/preview$/, { status: 409, body: { code: 'expired' } }],
  ['GET', /^\/api\/ai\/status$/, { available: true }],
  ['GET', /^\/api\/families$/, FAMILIES],
  ['GET', /^\/api\/home\/summary$/, HOME_SUMMARY],
  ['GET', /^\/api\/chat\/unread$/, [{ familyId: FAMILY_ID, count: 3 }]],
  ['POST', /^\/api\/chat\/hub-ticket$/, { status: 503, body: { title: 'нет realtime в стенде' } }],
  ['GET', /^\/api\/jobs\/active-summary$/, { extraction: emptyJobs, labAnalyte: emptyJobs, medication: emptyJobs, visitMedication: emptyJobs }],
  ['GET', /^\/api\/medication-courses\/attention-count$/, { count: 0 }],
  ['GET', /^\/api\/vaccinations\/attention-count$/, { count: 2 }],
  ['GET', /^\/api\/notifications\/unread-count$/, { count: 2 }],
  ['GET', /^\/api\/notifications$/, NOTIFICATIONS],
  ['GET', /^\/api\/search$/, { items: SEARCH, page: 1, pageSize: 15, totalCount: 3 }],
  ['GET', /^\/api\/health\/summary$/, HEALTH_SUMMARY],
  ['GET', /^\/api\/medical-records\/shares$/, [FAMILY_ID]],
  ['GET', /^\/api\/attachments\/limits$/, { maxFileSizeBytes: 20971520, maxFilesPerRecord: 10 }],
  ['GET', /^\/api\/medical-records\/doctors$/, ['Иванова Мария Петровна']],
  ['GET', /^\/api\/medical-records\/extraction-limits$/, { maxBatchDocuments: 10, maxActiveJobs: 3, activeNow: 0, dailyQuota: 30, usedToday: 2, resetsAt: '2026-10-15T00:00:00Z' }],
  ['GET', /^\/api\/medical-records$/, (_m, q) => {
    const items = q.get('kind') === 'visit' ? VISITS : RECORDS;
    return { items, totalCount: items.length, page: 1, pageSize: 20 };
  }],
  ['GET', /^\/api\/medical-records\/r-blood$/, R_BLOOD],
  ['GET', /^\/api\/medical-records\/v-1$/, VISITS[0]],
  ['GET', /^\/api\/medical-records\/v-1\/conclusion$/, {
    diagnosis: 'ОРВИ, лёгкое течение', anamnesis: 'Насморк и температура 37,5 три дня.', proceduresPerformed: null,
    recommendations: 'Обильное питьё, контроль через неделю.',
    prescribedMedications: [{ name: 'Нурофен', dosageInstructions: '200 мг при температуре выше 38,5', kbMedicationId: null }],
  }],
  ['GET', /^\/api\/medical-records\/[^/]+\/indicators$/, INDICATORS],
  ['GET', /^\/api\/medical-records\/[^/]+\/indicators\/[^/]+\/history$/, [
    { recordDate: '2026-04-10', valueRaw: '5,9', valueNumericText: '5.9', flag: 1, medicalRecordId: 'r-old' },
    { recordDate: '2026-07-02', valueRaw: '5,1', valueNumericText: '5.1', flag: 1, medicalRecordId: 'r-bio' },
    { recordDate: '2026-10-03', valueRaw: '4,24', valueNumericText: '4.24', flag: 1, medicalRecordId: 'r-blood' },
  ]],
  ['GET', /^\/api\/kb\/medications$/, { hasMore: false, items: [
    { id: 'kb-nurofen', displayName: 'Нурофен', purpose: 'Обезболивающее и жаропонижающее (ибупрофен).' },
    { id: 'kb-omeprazol', displayName: 'Омепразол', purpose: 'Снижает кислотность желудка.' },
    { id: 'kb-d3', displayName: 'Витамин D3', purpose: 'Восполняет дефицит витамина D.' },
    { id: 'kb-mg', displayName: 'Магний B6', purpose: null },
  ] }],
  ['GET', /^\/api\/kb\/analytes$/, { hasMore: false, items: [
    { id: 'wbc', displayName: 'Лейкоциты', specimenKbId: 'blood', specimenDisplayName: 'Кровь', plainExplanation: 'Белые клетки крови, защищают организм от инфекций.' },
    { id: 'esr', displayName: 'СОЭ', specimenKbId: 'blood', specimenDisplayName: 'Кровь', plainExplanation: 'Скорость оседания эритроцитов — маркер воспаления.' },
    { id: 'glu', displayName: 'Глюкоза', specimenKbId: 'blood', specimenDisplayName: 'Кровь', plainExplanation: null },
  ] }],
  ['GET', /^\/api\/indicators$/, [
    ...INDICATORS.slice(0, 12).map((i) => ({
      analyteKey: i.analyteKey, displayName: i.displayName, specimenKbId: 'blood', specimenDisplayName: 'Кровь',
      valueRaw: i.valueRaw, unit: i.unit, flag: i.flag, lastRecordDate: '2026-10-03', familyDependentId: null,
      targetUserId: null, patientName: 'Поляков Даниил',
    })),
    { analyteKey: 'glu', displayName: 'Глюкоза', specimenKbId: 'blood', specimenDisplayName: 'Кровь', valueRaw: '5,1',
      unit: 'ммоль/л', flag: 2, lastRecordDate: '2026-08-12', familyDependentId: null, targetUserId: VALERIA_ID,
      patientName: 'Полякова Валерия' },
  ]],
  ['GET', /^\/api\/indicators\/([^/]+)\/article$/, (m) => ({
    indicator: INDICATORS.find((i) => i.id === m[1]) ?? INDICATORS[1],
    patient: { ageYears: 29, sex: 0 }, matchedRefRangeIndex: 0, historyAvailable: true,
    article: {
      id: 'wbc', displayName: 'Лейкоциты', specimenKbId: 'blood', specimenDisplayName: 'Кровь', loincCode: '6690-2',
      defaultUnit: '×10⁹/л', plainExplanation: 'Белые клетки крови, защищают организм от инфекций.',
      whyMeasured: 'Показывают, есть ли воспаление или инфекция.',
      highMeans: 'Бывает при инфекциях и воспалениях.', lowMeans: 'Бывает после вирусных инфекций и при приёме некоторых лекарств.',
      refRanges: [{ ageFrom: 18, ageTo: null, sex: null, low: 4, high: 9, unit: '×10⁹/л', normKind: 0, population: 0, populationDetail: null, sourceDomain: null }],
      related: [], source: 'Справочник FamilyHub', updatedAt: '2026-09-01T00:00:00Z',
    },
  })],
  ['GET', /^\/api\/medical-records\/[^/]+\/attachments$/, [
    { id: 'a1', fileName: 'oak-03-10.pdf', contentType: 'application/pdf', sizeBytes: 182000, uploadedAt: '2026-10-03T09:00:00Z',
      extractedAt: '2026-10-03T09:02:00Z', previewStatus: 3 },
  ]],
  ['GET', /^\/api\/medical-records\/[^/]+\/summary$/, {
    plainSummary: 'Повышены СОЭ, эритроциты и лимфоциты, снижены лейкоциты и нейтрофилы.',
    deviations: [{ name: 'СОЭ', meaning: 'может говорить о воспалении' }],
    questionsForDoctor: ['Нужно ли пересдать общий анализ крови через месяц?'], disclaimer: 'Не является диагнозом.',
  }],
  ['GET', /^\/api\/medical-records\/[^/]+\/extraction$/, {
    status: 2, stage: 5, indicatorCount: 20, error: null, totalFiles: 1, processedFiles: 1,
    createdAt: '2026-10-03T09:00:00Z', completedAt: '2026-10-03T09:02:00Z', queuePosition: 0, waitingForAi: false,
  }],
  ['GET', /^\/api\/auth\/sessions$/, [
    { id: 's1', lastSeenAt: '2026-10-14T08:40:00Z', expiresAt: '2026-11-13T08:40:00Z', ipAddress: '85.140.1.10', device: 'Chrome · macOS', isCurrent: true },
    { id: 's2', lastSeenAt: '2026-10-12T19:05:00Z', expiresAt: '2026-11-11T19:05:00Z', ipAddress: '85.140.1.22', device: 'Safari · iPhone', isCurrent: false },
  ]],
  ['GET', /^\/api\/health-shares\/mine$/, [{ viewerUserId: VALERIA_ID, name: 'Валерия', categories: 1 }]],
  ['GET', /^\/api\/notifications\/preferences$/, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11].map((type) => ({ type, pushEnabled: type % 3 !== 2, telegramEnabled: false }))],
  ['GET', /^\/api\/health-shares\/shared-with-me$/, [{ ownerUserId: VALERIA_ID, name: 'Валерия', categories: 4 }]],
  ['GET', /^\/api\/families\/[^/]+\/current$/, MEMBERS],
  ['GET', /^\/api\/families\/[^/]+\/pending$/, []],
  ['GET', /^\/api\/families\/[^/]+\/dependents$/, []],
  ['GET', /^\/api\/families\/[^/]+\/birthdays$/, [
    { id: 'b1', familyId: FAMILY_ID, personName: 'Мама Дани (Алёна)', date: '1977-11-24', source: 0 },
    { id: 'b2', familyId: FAMILY_ID, personName: 'Папа Дани (Олег)', date: '1975-01-03', source: 0 },
    { id: 'b3', familyId: FAMILY_ID, personName: 'Валерия Полякова', date: '1998-03-14', source: 1 },
  ]],
  ['GET', /^\/api\/chat\/config$/, {
    maxTextLength: 4000, maxFilesPerMessage: 5, maxFileSizeBytes: 10485760, maxLinksPerMessage: 3,
    reactions: ['👍', '❤️', '🙏', '😊'], allowedContentTypes: ['image/jpeg', 'image/png', 'application/pdf'],
  }],
  ['GET', /^\/api\/families\/[^/]+\/chat\/messages$/, CHAT_PAGE],
  ['POST', /^\/api\/families\/[^/]+\/chat\/read$/, { status: 204, body: {} }],
  ['GET', /^\/api\/medication-courses\/today$/, INTAKE_TODAY],
  ['GET', /^\/api\/medication-courses$/, [COURSE]],
  ['GET', /^\/api\/medication-courses\/c-1$/, {
    summary: COURSE, notes: null, prescriptionText: null, source: null, medicationId: null, repeatAfterMinutes: 30,
    missedAfterMinutes: 120, lowStockDays: 5, timeZoneId: 'Europe/Moscow', nextBreakStart: null,
    adherence: { onTime: 4, counted: 4, percent: 100 }, watchers: [], canDelete: true,
  }],
  ['GET', /^\/api\/medication-courses\/c-1\/history$/, {
    from: '2026-10-01', to: '2026-10-14', adherence: { onTime: 4, counted: 4, percent: 100 },
    cells: ['10', '11', '12', '13', '14'].map((d) => ({ date: `2026-10-${d}`, time: '09:00', scheduledAt: `2026-10-${d}T09:00:00+03:00`, outcome: d === '14' ? 1 : 2, doseId: `c-1-${d}` })),
  }],
  ['GET', /^\/api\/vaccinations\/overview$/, VACC_OVERVIEW],
  ['GET', /^\/api\/vaccinations\/catalog$/, [
    { code: 'influenza', name: 'Грипп', shortName: 'Грипп', group: 1, diseases: ['Грипп'], doses: [], tradeNames: ['Совигрипп', 'Ультрикс'],
      about: 'Ежегодная прививка перед сезоном гриппа.', contraindications: null, reactionHint: 'Возможны слабость и температура до 37,5.',
      repeatEveryYears: null, seasonal: true, closedByDisease: false },
    { code: 'dtp', name: 'Дифтерия, столбняк', shortName: 'АДС-М', group: 0, diseases: ['Дифтерия', 'Столбняк'], doses: [], tradeNames: ['АДС-М'],
      about: null, contraindications: null, reactionHint: null, repeatEveryYears: 10, seasonal: false, closedByDisease: false },
    { code: 'mmr', name: 'Корь, краснуха, паротит', shortName: 'КПК', group: 0, diseases: ['Корь', 'Краснуха', 'Паротит'], doses: [], tradeNames: ['Вактривир'],
      about: null, contraindications: null, reactionHint: null, repeatEveryYears: null, seasonal: false, closedByDisease: true },
  ]],
  ['GET', /^\/api\/vaccinations\/people\/user\/[^/]+$/, VACC_PERSON],
  ['GET', /^\/api\/vaccinations\/people\/user\/[^/]+\/series\/[^/]+$/, VACC_SERIES],
  ['GET', /^\/api\/health-notes$/, HEALTH_NOTES],
  ['GET', /^\/api\/health-notes\/recent$/, []],
  ['GET', /^\/api\/health-notes\/catalog$/, HEALTH_NOTE_CATALOG],
  ['GET', /^\/api\/doctor-reports$/, DOCTOR_REPORTS],
  ['GET', /^\/api\/medkits\/mk1$/, { id: 'mk1', familyId: FAMILY_ID, name: 'Тестовая', createdByUserId: ME_ID, createdAt: '2026-08-25T10:00:00Z', medicationCount: 7 }],
  ['GET', /^\/api\/medkits\/mk1\/medications$/, [
    ['m1', 'Суматриптан', '2023-02-01', { type: 'таблетки', dose: '50 мг', quantity: '2 шт.' }],
    ['m2', 'Нурофен', '2026-11-01', { type: 'таблетки', dose: '200 мг', quantity: '10 шт.', instructions: 'При боли, не чаще 3 раз в день' }],
    ['m3', 'Омепразол', '2027-06-30', { type: 'капсулы', dose: '20 мг', quantity: '14 шт.' }],
    ['m4', 'Витамин D3', '2027-03-01', { type: 'капли', dose: '500 МЕ' }],
    ['m5', 'Магний B6', '2027-01-15', { quantity: '4 таблетки' }],
    ['m6', 'Бинт стерильный', null, {}],
    ['m7', 'Хлоргексидин', '2028-05-01', { type: 'раствор', dose: '0,05 %' }],
  ].map(([id, name, expiryDate, data]) => ({ id, medkitId: 'mk1', familyId: FAMILY_ID, name, expiryDate, data,
    createdByUserId: ME_ID, createdAt: '2026-08-25T10:00:00Z', enrichmentPending: false, enrichmentQueueAhead: 0 }))],
  ['GET', /^\/api\/families\/[^/]+\/medkits$/, [
    { id: 'mk1', familyId: FAMILY_ID, name: 'Тестовая', createdByUserId: ME_ID, createdAt: '2026-08-25T10:00:00Z', medicationCount: 7 },
  ]],
];

export function handleApi(method, pathname, query, body) {
  for (const [m, re, res] of ROUTES) {
    if (m !== method) continue;
    const match = pathname.match(re);
    if (!match) continue;
    const value = typeof res === 'function' ? res(match, query, body) : res;
    return structuredClone(value);
  }
  return undefined;
}
