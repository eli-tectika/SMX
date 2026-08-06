import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const DIR = dirname(fileURLToPath(import.meta.url));
const SHEETS = readdirSync(DIR).filter((f) => f.endsWith('.css'));

/**
 * A COMMENT THAT CLOSES TWICE SILENTLY DELETES THE RULE UNDER IT, and nothing else in this
 * repository can see it happen.
 *
 * This is not hypothetical — it shipped. `craft.css` carried a long explanatory comment that closed
 * with `*​/`, ran on for four more lines of prose, and closed again. CSS has no error recovery worth
 * the name: the parser took the orphaned prose as the start of a selector and swallowed everything up
 * to the next `{`, which belonged to the rule the comment was written to explain. The two-line clamp
 * on Regulatory's "Why" column was dropped entirely, and the column silently truncated the one token
 * its own source comment calls indispensable — the governing dimension's name.
 *
 * Every other gate stayed green while it did: `tsc` does not read CSS, Vite does not fail a build on
 * an unparseable selector, and jsdom reports no layout at all, so all 599 component tests passed
 * against a stylesheet with a missing rule. It was caught only by measuring computed style in a real
 * browser, which is not something the suite can do.
 *
 * So the check lives here, at the only level that can hold it cheaply: the text of the file.
 */
describe('stylesheet comments are balanced', () => {
  it.each(SHEETS)('%s has no stray or unclosed comment marker', (name) => {
    const css = readFileSync(join(DIR, name), 'utf8');

    let inComment = false;
    let line = 1;
    const strays: string[] = [];

    for (let i = 0; i < css.length; i += 1) {
      if (css[i] === '\n') line += 1;
      if (!inComment && css[i] === '/' && css[i + 1] === '*') {
        inComment = true;
        i += 1;
      } else if (inComment && css[i] === '*' && css[i + 1] === '/') {
        inComment = false;
        i += 1;
      } else if (!inComment && css[i] === '*' && css[i + 1] === '/') {
        // A close with nothing open. This is the shape that ate the clamp rule.
        strays.push(`line ${line}`);
        i += 1;
      }
    }

    expect(strays, `${name}: '*/' with no open comment at ${strays.join(', ')}`).toEqual([]);
    expect(inComment, `${name}: a comment is opened and never closed`).toBe(false);
  });
});

/**
 * The rule the bug above deleted, asserted by name.
 *
 * The balance check would have caught THAT instance, but a rule can go missing for other reasons —
 * a bad merge, an over-eager sweep, a selector renamed on one side only. This column is the one
 * place in the app where a clipped cell is load-bearing, so it is worth naming.
 */
describe('the Why column keeps its second line', () => {
  const craft = readFileSync(join(DIR, 'craft.css'), 'utf8');

  it('clamps .cellcol .cellclip to two lines', () => {
    const rule = craft.match(/\.cellcol\s+\.cellclip\s*\{[^}]*\}/);
    expect(rule, '.cellcol .cellclip rule is missing from craft.css').not.toBeNull();
    expect(rule![0]).toContain('-webkit-line-clamp: 2');
    // Without `white-space: normal` the clamp has nothing to wrap and silently stays one line.
    expect(rule![0]).toContain('white-space: normal');
  });

  it('leaves the full matrix on one line, where width is not the scarce thing', () => {
    expect(craft).toMatch(/\.mx--compact\s+\.cellcol\s+\.cellclip\s*\{[^}]*white-space:\s*nowrap/);
  });
});
