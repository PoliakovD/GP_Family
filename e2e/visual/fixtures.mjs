// Фикстуры API для visual/shots.mjs. Данные повторяют макеты docs/redesign/screens/png/:
// семья «Поляковы» (Даниил — админ, Валерия — участник), просроченное лекарство, анализ крови
// с 6 показателями вне нормы, прививка от гриппа, дни рождения. «Сегодня» — 14.10.2026 (shots.mjs).
//
// Неизвестный запрос → undefined → shots.mjs отвечает 404 и печатает его в конце прогона:
// так видно, какую фикстуру добавить для нового экрана.

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
  { name: 'intake', path: '/health/intake' },
  { name: 'intake-form', path: '/health/intake', prepare: async (page) => {
    await page.getByRole('button', { name: 'Новый курс' }).click();
    await page.getByRole('radio', { name: /Вручную/ }).click();
    await page.waitForTimeout(400);
  } },
  { name: 'intake-course', path: '/health/intake/courses/c-1', only: 'mobile' },
  { name: 'vaccinations', path: '/health/vaccinations' },
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
  // Витрина компонентов (только dev-сборка, фаза 2 редизайна).
  { name: 'dev-ui', path: '/dev/ui', spinner: true }, // спиннер — часть витрины
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

/** [метод, регулярка пути, ответ | (match, query, body) => ответ] */
const ROUTES = [
  ['GET', /^\/api\/auth\/me$/, ME],
  ['GET', /^\/api\/consents\/status$/, { accepted: true, version: '1' }],
  ['GET', /^\/api\/ai\/status$/, { available: true }],
  ['GET', /^\/api\/families$/, FAMILIES],
  ['GET', /^\/api\/home\/summary$/, HOME_SUMMARY],
  ['GET', /^\/api\/chat\/unread$/, [{ familyId: FAMILY_ID, count: 3 }]],
  ['POST', /^\/api\/chat\/hub-ticket$/, { status: 503, body: { title: 'нет realtime в стенде' } }],
  ['GET', /^\/api\/jobs\/active-summary$/, { extraction: emptyJobs, labAnalyte: emptyJobs, medication: emptyJobs, visitMedication: emptyJobs }],
  ['GET', /^\/api\/medication-courses\/attention-count$/, { count: 0 }],
  ['GET', /^\/api\/vaccinations\/attention-count$/, { count: 2 }],
  ['GET', /^\/api\/notifications\/unread-count$/, { count: 2 }],
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
  ['GET', /^\/api\/vaccinations\/catalog$/, []],
  ['GET', /^\/api\/vaccinations\/people\/user\/[^/]+$/, VACC_PERSON],
  ['GET', /^\/api\/vaccinations\/people\/user\/[^/]+\/series\/[^/]+$/, VACC_SERIES],
  ['GET', /^\/api\/health-notes$/, HEALTH_NOTES],
  ['GET', /^\/api\/health-notes\/recent$/, []],
  ['GET', /^\/api\/health-notes\/catalog$/, HEALTH_NOTE_CATALOG],
  ['GET', /^\/api\/doctor-reports$/, DOCTOR_REPORTS],
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
