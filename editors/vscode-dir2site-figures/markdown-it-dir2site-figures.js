// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

/**
 * markdown-it plugin reproducing the two Markdig features dir2site relies on for figures, so
 * VS Code's preview matches the generated site.
 *
 *   ^^^
 *   ![](_media/portrait.jpg){.figure-right width=220}
 *   ^^^ Albert Einstein, c. 1947
 *
 * becomes
 *
 *   <figure>
 *   <p><img src="_media/portrait.jpg" class="figure-right" width="220" alt="" /></p>
 *   <figcaption>Albert Einstein, c. 1947</figcaption>
 *   </figure>
 *
 * Two rules are needed because Markdig splits the work between two extensions: Figures supplies
 * `^^^`, and GenericAttributes supplies `{…}`. Both are written out longhand rather than pulled
 * from npm so the extension ships with no dependencies to install or bundle.
 */

const FENCE = '^^^';

/** `^^^` … `^^^ caption` → figure / figcaption. */
function figureBlock(state, startLine, endLine, silent) {
  const start = state.bMarks[startLine] + state.tShift[startLine];
  const max = state.eMarks[startLine];

  if (start + FENCE.length > max) return false;
  if (state.src.slice(start, start + FENCE.length) !== FENCE) return false;

  // The opening fence carries nothing — anything trailing means this isn't a figure.
  if (state.src.slice(start + FENCE.length, max).trim().length !== 0) return false;
  if (silent) return true;

  let closeLine = startLine;
  let caption = '';
  let found = false;
  while (++closeLine < endLine) {
    const s = state.bMarks[closeLine] + state.tShift[closeLine];
    const e = state.eMarks[closeLine];
    if (state.src.slice(s, s + FENCE.length) === FENCE) {
      caption = state.src.slice(s + FENCE.length, e).trim();
      found = true;
      break;
    }
  }

  // Unterminated: leave it alone rather than swallowing the rest of the document.
  if (!found) return false;

  const oldLineMax = state.lineMax;
  const oldParent = state.parentType;
  state.lineMax = closeLine;
  state.parentType = 'figure';

  const open = state.push('figure_open', 'figure', 1);
  open.map = [startLine, closeLine];

  state.md.block.tokenize(state, startLine + 1, closeLine);

  if (caption) {
    state.push('figcaption_open', 'figcaption', 1);
    const inline = state.push('inline', '', 0);
    inline.content = caption;
    inline.map = [closeLine, closeLine + 1];
    inline.children = [];
    state.push('figcaption_close', 'figcaption', -1);
  }

  state.push('figure_close', 'figure', -1);

  state.lineMax = oldLineMax;
  state.parentType = oldParent;
  state.line = closeLine + 1;
  return true;
}

/**
 * Parses `{.class #id key=value}` into [name, value] pairs.
 * Returns null when the braces don't close, so malformed input stays visible as text rather than
 * silently vanishing.
 */
function parseAttrs(text) {
  if (text[0] !== '{') return null;
  const end = text.indexOf('}');
  if (end === -1) return null;

  const attrs = [];
  const body = text.slice(1, end).trim();

  for (const part of body.split(/\s+/).filter(Boolean)) {
    if (part[0] === '.') {
      attrs.push(['class', part.slice(1)]);
    } else if (part[0] === '#') {
      attrs.push(['id', part.slice(1)]);
    } else {
      const eq = part.indexOf('=');
      if (eq > 0) {
        // Quotes are optional in Markdig, so strip them if present.
        attrs.push([part.slice(0, eq), part.slice(eq + 1).replace(/^["']|["']$/g, '')]);
      }
    }
  }

  return { attrs, length: end + 1 };
}

/** Applies a trailing `{…}` to the image it follows, the way Markdig's GenericAttributes does. */
function imageAttrs(state) {
  for (const block of state.tokens) {
    if (block.type !== 'inline' || !block.children) continue;

    const children = block.children;
    for (let i = 0; i < children.length - 1; i++) {
      if (children[i].type !== 'image') continue;

      const next = children[i + 1];
      if (next.type !== 'text') continue;

      const parsed = parseAttrs(next.content);
      if (!parsed) continue;

      for (const [name, value] of parsed.attrs) {
        // Several `.class` entries accumulate, matching Markdig.
        if (name === 'class' && children[i].attrGet('class')) {
          children[i].attrSet('class', children[i].attrGet('class') + ' ' + value);
        } else {
          children[i].attrSet(name, value);
        }
      }

      next.content = next.content.slice(parsed.length);
    }
  }
}

module.exports = function dir2siteFigures(md) {
  md.block.ruler.before('fence', 'dir2site_figure', figureBlock, {
    alt: ['paragraph', 'reference', 'blockquote', 'list'],
  });
  md.core.ruler.after('inline', 'dir2site_image_attrs', imageAttrs);
};
