using LightMarkdownWriter.Services;

namespace LightMarkdownWriter.Tests;

/// <summary>Emoji, math markup, heading anchor ids and math in standalone exports.</summary>
public sealed class Milestone10Tests
{
    // ---------- 10.1 Emoji ----------

    [Fact]
    public void Emoji_shortcodes_render_but_smileys_stay_as_typed()
    {
        var html = MarkdownRenderer.ToHtml("Ship it :tada: :rocket: and smile :) ;)");
        Assert.Contains("🎉", html);
        Assert.Contains("🚀", html);
        Assert.Contains(":) ;)", html); // GitHub doesn't convert smileys, so neither do we
    }

    [Fact]
    public void Unknown_shortcodes_and_text_with_colons_are_left_alone()
    {
        var html = MarkdownRenderer.ToHtml("Time 10:30:45 and :not_an_emoji_name:");
        Assert.Contains("10:30:45", html);
        Assert.Contains(":not_an_emoji_name:", html);
    }

    // ---------- 10.2 Math ----------

    [Fact]
    public void Inline_and_block_math_become_math_elements_for_katex()
    {
        var html = MarkdownRenderer.ToHtml("Inline $a^2 + b^2 = c^2$.\n\n$$\n\\int_0^1 x\\,dx\n$$");
        Assert.Contains(@"<span class=""math"">\(a^2 + b^2 = c^2\)</span>", html);
        Assert.Matches(@"<div class=""math""[^>]*>\s*\\\[\s*\\int_0\^1 x\\,dx\s*\\\]</div>", html);
    }

    [Fact]
    public void Dollar_amounts_are_not_treated_as_math()
    {
        var html = MarkdownRenderer.ToHtml("It costs $5 and $10 today.");
        Assert.DoesNotContain("class=\"math\"", html);
    }

    [Fact]
    public void Standalone_export_embeds_katex_css_and_fonts_only_when_there_is_math()
    {
        var withMath = ExportService.BuildStandaloneHtml("<p><span class=\"katex\">x</span></p>", "Math", null);
        var without = ExportService.BuildStandaloneHtml("<p>plain</p>", "Plain", null);

        Assert.Contains(".katex", withMath);
        Assert.Contains("url(data:font/woff2;base64,", withMath);
        Assert.DoesNotContain("data:font/woff2", without);
        Assert.True(withMath.Length > without.Length + 200_000); // the fonts really are inside
    }

    // ---------- 10.3 Heading anchor ids ----------

    [Fact]
    public void Headings_carry_the_same_anchor_ids_the_html_uses()
    {
        var result = MarkdownRenderer.Render("# Getting Started\n\n## Install *it* now\n\n## Getting Started");

        Assert.Equal(["getting-started", "install-it-now", "getting-started-1"], result.Headings.Select(h => h.Id));
        foreach (var heading in result.Headings)
            Assert.Contains($"id=\"{heading.Id}\"", result.Html); // a link's #fragment finds the same heading
    }
}
