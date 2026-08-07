// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

const vscode = require('vscode');
const dir2siteFigures = require('./markdown-it-dir2site-figures');

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
      const config = vscode.workspace.getConfiguration('dir2siteFigures');
      if (config.get('hardLineBreaks', true)) {
        md.set({ breaks: true });
        // The option alone doesn't hold: VS Code applies its own markdown.preview.breaks to this
        // same instance after extendMarkdownIt returns, so a newline went back to being a space
        // and hand-wrapped paragraphs previewed as one long line while the site broke them.
        // Nothing re-applies renderer rules, so setting the rule is what actually sticks.
        md.renderer.rules.softbreak = () => '<br>\n';
      }

      return md.use(dir2siteFigures);
    },
  };
}

function deactivate() {}

module.exports = { activate, deactivate };
