import { describe, expect, it } from 'vitest';
import { documentName } from './documentName';

const enc = (payload: string) =>
  btoa(String.fromCharCode(...new TextEncoder().encode(payload)))
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '');

describe('documentName', () => {
  it('names a regulatory document by its docId', () => {
    expect(documentName(`reg_${enc('eur-lex/reach-annex-xvii')}`)).toBe('reach-annex-xvii');
  });

  it('names a seeded document by its docId', () => {
    expect(documentName(`seed_${enc('eu/sml-list')}`)).toBe('sml-list');
  });

  /** A safety sheet has no docId — its identity is the substance and who published it. */
  it('names an SDS by substance and supplier', () => {
    expect(documentName(`sds_${enc('1314-36-9|Alfa Aesar|2024-01-05')}`)).toBe(
      '1314-36-9 · Alfa Aesar',
    );
  });

  /**
   * EVERY FAILURE IS `null`, and `null` means the chip stays inert. Guessing a name from a
   * half-readable id would put a link on screen that opens nothing, and the operator only finds out
   * after following it.
   */
  it.each([
    ['absent', undefined],
    ['null', null],
    ['empty', ''],
    ['no kind separator', 'regZXVyLWxleA'],
    ['empty payload', 'reg_'],
    ['a kind this build does not know', `nosuchkind_${enc('a/b')}`],
    ['a gap, which names no document', `sdsgap_${enc('Y_oxide')}`],
    ['the wrong segment count for its kind', `reg_${enc('eur-lex')}`],
    ['an empty segment', `reg_${enc('eur-lex/')}`],
    ['not base64 at all', 'reg_!!!!'],
  ])('answers null for %s', (_label, id) => {
    expect(documentName(id as string | null | undefined)).toBeNull();
  });

  /** Invalid UTF-8 must fail rather than become U+FFFD inside a rendered file name. */
  it('answers null for bytes that are not valid UTF-8', () => {
    expect(documentName('reg_' + btoa('\xff\xfe/x').replace(/\+/g, '-').replace(/\//g, '_'))).toBeNull();
  });
});
