// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using dir2site.Services;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace dir2site.Tests;

/// <summary>
/// The point of the editor is that a user's hand-written YAML survives the app writing to it,
/// so most of these assert on what did <em>not</em> change.
/// </summary>
public class YamlDocumentEditorTests
{
    // Normalised deliberately: this is a source literal, and git checks .cs out with CRLF on
    // Windows — so without this the "LF document" fixture would silently be a CRLF one there, and
    // the tests would assert the opposite of what they claim.
    private static readonly string Annotated = Lf(
        """
        # dir2site config — hand-edited, with notes I want to keep
        title: My Site

        # The footer shows on every page
        footer: © 2026

        primaryColor: '#333333'   # deliberately quoted, it starts with a hash
        myOwnKey: something dir2site knows nothing about
        """);

    /// <summary>Forces LF, whatever the checkout did to this file.</summary>
    private static string Lf(string s) => s.Replace("\r\n", "\n");

    private static string Value(string yaml, string key)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        return ((YamlScalarNode)root.Children[new YamlScalarNode(key)]).Value!;
    }

    [Fact]
    public void ChangingOneValue_LeavesEveryComment_BlankLine_AndOtherKeyAlone()
    {
        var editor = YamlDocumentEditor.TryLoad(Annotated)!;

        Assert.True(editor.Set("title", "Renamed Site"));

        var result = editor.Text;
        Assert.Contains("# dir2site config — hand-edited, with notes I want to keep", result);
        Assert.Contains("# The footer shows on every page", result);
        Assert.Contains("# deliberately quoted, it starts with a hash", result);
        Assert.Contains("myOwnKey: something dir2site knows nothing about", result);
        Assert.Contains("\n\n# The footer", result);            // blank line preserved
        Assert.Equal("Renamed Site", Value(result, "title"));
        Assert.Equal("© 2026", Value(result, "footer"));        // untouched value intact
        Assert.Equal("#333333", Value(result, "primaryColor"));
    }

    [Fact]
    public void SettingTheValueItAlreadyHas_ChangesNothing()
    {
        var editor = YamlDocumentEditor.TryLoad(Annotated)!;

        Assert.True(editor.Set("title", "My Site"));

        Assert.False(editor.IsModified);   // so the caller can skip the write entirely
        Assert.Equal(Annotated, editor.Text);
    }

    [Fact]
    public void AddingAKey_AppendsWithoutDisturbingWhatWasThere()
    {
        var editor = YamlDocumentEditor.TryLoad(Annotated)!;

        Assert.True(editor.Set("siteUrl", "https://example.com"));

        Assert.Contains("myOwnKey: something dir2site knows nothing about", editor.Text);
        Assert.Contains("# The footer shows on every page", editor.Text);
        Assert.Equal("https://example.com", Value(editor.Text, "siteUrl"));
    }

    [Theory]
    [InlineData("has: a colon")]
    [InlineData("trailing hash #")]
    [InlineData("#leading hash")]
    [InlineData(" leading space")]
    [InlineData("trailing space ")]
    [InlineData("123")]              // must stay a string, not become a number
    [InlineData("true")]             // must stay a string, not become a bool
    [InlineData("")]
    [InlineData("emoji 🎉 and ünïcode")]
    [InlineData("quote \" and ' apostrophe")]
    [InlineData("back\\slash")]
    public void AwkwardValues_RoundTripAndStillParse(string awkward)
    {
        var editor = YamlDocumentEditor.TryLoad(Annotated)!;

        Assert.True(editor.Set("title", awkward));

        Assert.Equal(awkward, Value(editor.Text, "title"));
        Assert.Equal("© 2026", Value(editor.Text, "footer"));   // neighbours unharmed
    }

    [Fact]
    public void CrlfFile_KeepsCrlf()
    {
        var crlf = Annotated.Replace("\n", "\r\n");
        var editor = YamlDocumentEditor.TryLoad(crlf)!;

        Assert.True(editor.Set("title", "Renamed"));

        Assert.DoesNotContain("\n\n", editor.Text.Replace("\r\n", "\n").Replace("\n\n", "<blank>"));
        Assert.Contains("\r\n", editor.Text);
        Assert.Equal("Renamed", Value(editor.Text, "title"));
    }

    [Fact]
    public void LfFile_GainsNoCarriageReturns()
    {
        var editor = YamlDocumentEditor.TryLoad(Annotated)!;

        Assert.True(editor.Set("title", "Renamed"));

        Assert.DoesNotContain('\r', editor.Text);
    }

    [Fact]
    public void BlockScalar_IsReplacedWithoutWeldingTheNextLine()
    {
        var yaml = Lf(
            """
            footer: |
              line one
              line two
            title: After The Block
            """);
        var editor = YamlDocumentEditor.TryLoad(yaml)!;

        Assert.True(editor.Set("footer", "now single line"));

        Assert.Equal("now single line", Value(editor.Text, "footer"));
        Assert.Equal("After The Block", Value(editor.Text, "title"));
    }

    [Fact]
    public void MultiLineValue_IsWrittenAsABlockScalar()
    {
        var editor = YamlDocumentEditor.TryLoad(Annotated)!;

        Assert.True(editor.Set("footer", "first line\nsecond line"));

        Assert.Equal("first line\nsecond line", Value(editor.Text, "footer").TrimEnd('\n'));
        Assert.Equal("My Site", Value(editor.Text, "title"));
    }

    [Fact]
    public void NonScalarValue_IsRefusedRatherThanOverwritten()
    {
        var yaml = Lf(
            """
            deploy:
              host: 127.0.0.1
            title: Keep Me
            """);
        var editor = YamlDocumentEditor.TryLoad(yaml)!;

        Assert.False(editor.Set("deploy", "clobbered"));

        Assert.False(editor.IsModified);
        Assert.Equal(yaml, editor.Text);
    }

    [Fact]
    public void UnparseableInput_IsRefusedAtLoad()
    {
        Assert.Null(YamlDocumentEditor.TryLoad("key: [unclosed\n  nonsense: {"));
        Assert.Null(YamlDocumentEditor.TryLoad("- just\n- a\n- sequence"));  // no root mapping
    }

    [Fact]
    public void AddingAKeyToADocumentEndingInABlock_StaysSurgical()
    {
        // Regression: AddKey used to insert at the last value node's End.Index. For a block that
        // index points at the *start* of its content, so the key landed mid-block, the re-parse
        // failed, and the caller fell back to a whole-file rewrite — losing every comment. This is
        // the normal shape of a dir2site.yaml, since `deploy:` is always appended last.
        var yaml = Lf(
            """
            # notes I want to keep
            title: My Site
            deploy:
              active: production
              targets:
              - name: production
                host: 127.0.0.1
            """);
        var editor = YamlDocumentEditor.TryLoad(yaml)!;

        Assert.True(editor.Set("siteUrl", "https://example.com"));

        Assert.True(editor.IsModified);
        Assert.Contains("# notes I want to keep", editor.Text);
        Assert.Equal("https://example.com", Value(editor.Text, "siteUrl"));
        Assert.Equal("My Site", Value(editor.Text, "title"));

        var stream = new YamlStream();
        stream.Load(new StringReader(editor.Text));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        Assert.True(root.Children.ContainsKey(new YamlScalarNode("deploy")));
    }

    [Fact]
    public void AddingAKeyToACrlfDocument_UsesCrlf()
    {
        var editor = YamlDocumentEditor.TryLoad(Annotated.Replace("\n", "\r\n"))!;

        Assert.True(editor.Set("siteUrl", "https://example.com"));

        // A stray bare LF would leave the file with mixed endings and churn every diff on Windows.
        Assert.DoesNotContain("\n", editor.Text.Replace("\r\n", ""));
    }

    [Fact]
    public void SetBlockOnACrlfDocument_UsesCrlf()
    {
        var editor = YamlDocumentEditor.TryLoad(Annotated.Replace("\n", "\r\n"))!;

        Assert.True(editor.SetBlock("deploy", "active: production\ntargets:\n- name: production"));

        Assert.DoesNotContain("\n", editor.Text.Replace("\r\n", ""));
        Assert.Contains("# The footer shows on every page", editor.Text);
    }

    [Fact]
    public void SetAll_AppliesEveryUpdate()
    {
        var editor = YamlDocumentEditor.TryLoad(Annotated)!;

        Assert.True(editor.SetAll([
            new("title", "One"),
            new("footer", "Two"),
            new("brandNew", "Three"),
        ]));

        Assert.Equal("One",   Value(editor.Text, "title"));
        Assert.Equal("Two",   Value(editor.Text, "footer"));
        Assert.Equal("Three", Value(editor.Text, "brandNew"));
        Assert.Contains("myOwnKey:", editor.Text);
        Assert.Contains("# The footer shows on every page", editor.Text);
    }
}
