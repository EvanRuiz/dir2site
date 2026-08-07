// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

/**
 * Checks the plugin against what dir2site's Markdig pipeline actually emits.
 *
 * Run with:  node test.js          (needs markdown-it on NODE_PATH, or a local npm i markdown-it)
 *
 * The expectations below were captured from the real renderer, then normalised for the two
 * cosmetic differences that don't affect rendering: markdown-it orders attributes differently and
 * doesn't self-close void elements.
 */

const assert = require('node:assert');
const MarkdownIt = require('markdown-it');
const plugin = require('./markdown-it-dir2site-figures');

// `breaks` mirrors dir2site's UseSoftlineBreakAsHardlineBreak, which the extension turns on.
const md = new MarkdownIt({ html: true, breaks: true }).use(plugin);

/** Order-insensitive comparison of the tags and attributes that matter. */
function normalise(html) {
  return html
    .replace(/\s*\/>/g, '>')
    .replace(/<img([^>]*)>/g, (_, attrs) => {
      const pairs = [...attrs.matchAll(/([\w-]+)="([^"]*)"/g)]
        .map(([, k, v]) => `${k}="${v}"`)
        .sort();
      return `<img ${pairs.join(' ')}>`;
    })
    .replace(/\s+/g, ' ')
    .trim();
}

let failures = 0;
function checkWith(instance, name, src, expected) {
  const actual = normalise(instance.render(src));
  try {
    assert.strictEqual(actual, normalise(expected));
    console.log(`  ok   ${name}`);
  } catch {
    failures++;
    console.log(`  FAIL ${name}`);
    console.log(`       expected: ${normalise(expected)}`);
    console.log(`       actual:   ${actual}`);
  }
}

const check = (name, src, expected) => checkWith(md, name, src, expected);

console.log('dir2site figure plugin');

check(
  'figure with caption and attributes — the README example',
  'Intro paragraph.\n\n^^^\n![](_media/portrait.jpg){.figure-right width=220}\n^^^ Albert Einstein, c. 1947\n\nBody text after.\n',
  '<p>Intro paragraph.</p>\n<figure>\n<p><img src="_media/portrait.jpg" class="figure-right" width="220" alt="" /></p>\n<figcaption>Albert Einstein, c. 1947</figcaption>\n</figure>\n<p>Body text after.</p>'
);

check(
  'figure without a caption emits no figcaption',
  '^^^\n![](a.jpg){.figure-center}\n^^^\n',
  '<figure>\n<p><img src="a.jpg" class="figure-center" alt="" /></p>\n</figure>'
);

check(
  'an unterminated fence is left as text rather than swallowing the document',
  '^^^\n![](a.jpg)\n\nstill going\n',
  '<p>^^^<br>\n<img src="a.jpg" alt="" /></p>\n<p>still going</p>'
);

check(
  'a caret run inside a paragraph is not a fence',
  'A caret ^^^ mid sentence.\n',
  '<p>A caret ^^^ mid sentence.</p>'
);

check(
  'images without attributes are untouched',
  '![alt](a.jpg)\n',
  '<p><img src="a.jpg" alt="alt" /></p>'
);

check(
  'id, class and quoted values all parse',
  '![](a.jpg){#hero .figure-left width="300"}\n',
  '<p><img src="a.jpg" id="hero" class="figure-left" width="300" alt="" /></p>'
);

check(
  'caption text is parsed as markdown',
  '^^^\n![](a.jpg)\n^^^ Portrait, *c.* 1947\n',
  '<figure>\n<p><img src="a.jpg" alt="" /></p>\n<figcaption>Portrait, <em>c.</em> 1947</figcaption>\n</figure>'
);

check(
  'a single newline becomes a break, as dir2site renders it',
  'First line\nSecond line\n',
  '<p>First line<br>\nSecond line</p>'
);

check(
  'a blank line still starts a new paragraph',
  'First para\n\nSecond para\n',
  '<p>First para</p>\n<p>Second para</p>'
);

check(
  'a caption written under an image stays on its own line',
  '![fig](_media/figure.webp)\nA caption\n',
  '<p><img src="_media/figure.webp" alt="fig" /><br>\nA caption</p>'
);

check(
  'a malformed attribute block stays visible instead of vanishing',
  '![](a.jpg){.unclosed\n',
  '<p><img src="a.jpg" alt="" />{.unclosed</p>'
);

/**
 * The extension itself, with `vscode` stubbed. This exercises the one thing the plugin can't:
 * VS Code hands extendMarkdownIt a live instance and then applies its own markdown.preview.breaks
 * to it afterwards, so an instance built without `breaks` — and re-set without it — still has to
 * come out breaking lines the way dir2site does.
 */
const Module = require('node:module');
const load = Module._load;
Module._load = (request, ...rest) =>
  request === 'vscode'
    ? { workspace: { getConfiguration: () => ({ get: (_, fallback) => fallback }) } }
    : load(request, ...rest);
const extension = require('./extension');
Module._load = load;

const hosted = new MarkdownIt({ html: true });
extension.activate().extendMarkdownIt(hosted);
hosted.set({ breaks: false }); // what VS Code does to us after we've had our turn

checkWith(
  hosted,
  'lines still break after the host resets the option',
  'First line\nSecond line\n',
  '<p>First line<br>\nSecond line</p>'
);

console.log(failures === 0 ? '\nall passed' : `\n${failures} failed`);
process.exit(failures === 0 ? 0 : 1);
