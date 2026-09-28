import { describe, expect, it } from 'vitest';
import type { CurrentMember, FamilyDependent, FamilySummary } from '../../models/types';
import { buildPatientOptions, SELF_PATIENT_OPTION } from './patient-options';

function family(overrides: Partial<FamilySummary> & { id: string; name: string }): FamilySummary {
  return { myRole: 0, myStatus: 0, currentMembers: null, dependents: null, ...overrides };
}

function dependent(overrides: Partial<FamilyDependent> & { id: string; firstName: string }): FamilyDependent {
  return {
    familyId: 'f1', lastName: null, middleName: null, gender: 0, birthDate: null,
    isPet: false, petSpecies: null, createdByUserId: 'u0', createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

function member(overrides: Partial<CurrentMember> & { id: string }): CurrentMember {
  return { lastName: null, firstName: null, middleName: null, username: null, role: 1, joinedAt: '2026-01-01T00:00:00Z', ...overrides };
}

describe('buildPatientOptions', () => {
  it('always starts with the SELF option, even with no families', () => {
    expect(buildPatientOptions([], 'u1')).toEqual([SELF_PATIENT_OPTION]);
  });

  it('adds a dependent option labelled with the family name', () => {
    const options = buildPatientOptions(
      [family({ id: 'f1', name: 'Ивановы', dependents: [dependent({ id: 'd1', firstName: 'Аня', lastName: 'Иванова' })] })],
      'u1',
    );

    expect(options).toHaveLength(2);
    expect(options[1]).toMatchObject({ key: 'dep:d1', familyDependentId: 'd1', targetUserId: null, label: 'Иванова Аня (Ивановы)' });
  });

  it('uses the pet name as-is, without a last name, for a pet dependent', () => {
    const options = buildPatientOptions(
      [family({ id: 'f1', name: 'Ивановы', dependents: [dependent({ id: 'd1', firstName: 'Барсик', isPet: true, lastName: 'что-то' })] })],
      'u1',
    );

    expect(options[1]).toMatchObject({ label: 'Барсик (Ивановы)', avatarLastName: null, shortLabel: 'Барсик' });
  });

  it('excludes myUserId from the member list — I am already the SELF option', () => {
    const options = buildPatientOptions(
      [family({ id: 'f1', name: 'Ивановы', currentMembers: [member({ id: 'u1' }), member({ id: 'u2', firstName: 'Пётр' })] })],
      'u1',
    );

    expect(options.map((o) => o.key)).toEqual(['self', 'user:u2']);
  });

  it('dedupes a member shared across two active families into a single option', () => {
    const shared = member({ id: 'u2', firstName: 'Пётр' });
    const options = buildPatientOptions(
      [
        family({ id: 'f1', name: 'Ивановы', currentMembers: [shared] }),
        family({ id: 'f2', name: 'Петровы', currentMembers: [shared] }),
      ],
      'u1',
    );

    expect(options.filter((o) => o.key === 'user:u2')).toHaveLength(1);
  });
});
