// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

const vscode = require('vscode');
const dir2site = require('./markdown-it-dir2site');

/**
 * Whether a single newline should render as a line break.
 *
 * The setting moved from `dir2siteFigures.` to `dir2siteMarkdown.` when the extension stopped being
 * only about figures. Renaming a setting silently discards whatever the user chose, so the old key
 * is still honoured — but only while the new one is left alone, so setting the new one is always
 * what wins.
 */
function hardLineBreaks() {
  const config = vscode.workspace.getConfiguration();

  // inspect is what separates "set to true" from "left alone", and only it can tell them apart.
  // Guarded because a host that doesn't provide it must still get a preview rather than an
  // exception — the setting defaults to on, so the fallback path is the ordinary answer anyway.
  const current = typeof config.inspect === 'function'
    ? config.inspect('dir2siteMarkdown.hardLineBreaks')
    : undefined;
  const chosenHere = current !== undefined &&
    (current.globalValue ?? current.workspaceValue ?? current.workspaceFolderValue) !== undefined;

  if (chosenHere) return config.get('dir2siteMarkdown.hardLineBreaks');

  // ?? rather than ||: the whole point of the old key is someone who set it to false.
  return config.get('dir2siteFigures.hardLineBreaks') ?? true;
}

/**
 * VS Code calls extendMarkdownIt on the object returned from activate, which is how an extension
 * adds syntax to the built-in preview rather than replacing it.
 */
function activate() {
  return {
    extendMarkdownIt(md) {
      // dir2site renders with UseSoftlineBreakAsHardlineBreak, so a single newline is a <br> and a
      // hand-wrapped paragraph keeps its shape. markdown-it calls the same thing `breaks`.
      //
      // This is a setting because it applies to every Markdown file previewed in the window, not
      // just dir2site ones — someone who edits both will want a say. Changing it takes effect when
      // the preview is next created.
      if (hardLineBreaks()) {
        md.set({ breaks: true });
        // The option alone doesn't hold: VS Code applies its own markdown.preview.breaks to this
        // same instance after extendMarkdownIt returns, so a newline went back to being a space
        // and hand-wrapped paragraphs previewed as one long line while the site broke them.
        // Nothing re-applies renderer rules, so setting the rule is what actually sticks.
        md.renderer.rules.softbreak = () => '<br>\n';
      }

      return md.use(dir2site);
    },
  };
}

function deactivate() {}

module.exports = { activate, deactivate };
