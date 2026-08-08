using System.Globalization;
using SessionStories.Core.Generation;
using SessionStories.Providers.Claude;

namespace SessionStories.Tests;

public class ClaudeArgumentBuilderTests
{
    private static string ValueAfter(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == flag)
                return args[i + 1];
        throw new Xunit.Sdk.XunitException($"Flag '{flag}' not found in argument list.");
    }

    [Fact]
    public void SceneWithResume_ProducesExactArgumentList()
    {
        var context = new StageContext(
            StoryId: "2026-08-08-marrowfield",
            Variant: "kid1",
            SceneId: "s3",
            ResumeSessionId: "sess-abc-123");

        var args = ClaudeArgumentBuilder.Build(GenerationStage.Scene, context, 0.50m);

        Assert.Equal(
        [
            "-p",
            "Stage: scene. Story: library/stories/2026-08-08-marrowfield. Variant: kid1. Scene: s3. Follow skills/story/SKILL.md.",
            "--output-format", "stream-json",
            "--verbose",
            "--permission-mode", "acceptEdits",
            "--allowedTools", "Read,Write(library/stories/2026-08-08-marrowfield/**)",
            "--max-budget-usd", "0.50",
            "--resume", "sess-abc-123",
        ], args);
    }

    [Fact]
    public void Regen_NeverResumes_EvenWhenResumeSessionIdIsSet()
    {
        var context = new StageContext(
            StoryId: "2026-08-08-marrowfield",
            Variant: "kid1",
            SceneId: "s2",
            ResumeSessionId: "sess-should-be-ignored");

        var args = ClaudeArgumentBuilder.Build(GenerationStage.Regen, context, 0.50m);

        Assert.DoesNotContain("--resume", args);
        Assert.DoesNotContain("sess-should-be-ignored", args);
        Assert.StartsWith("Stage: regen. ", ValueAfter(args, "-p"));
    }

    [Fact]
    public void SceneWithoutResumeSessionId_HasNoResumeFlag()
    {
        var context = new StageContext("marrowfield", "base", SceneId: "s1");

        var args = ClaudeArgumentBuilder.Build(GenerationStage.Scene, context, 0.50m);

        Assert.DoesNotContain("--resume", args);
    }

    [Fact]
    public void BudgetFormatting_UsesInvariantCulture_UnderCommaDecimalLocale()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // pl-PL formats 0.35 as "0,35" — the CLI would reject that.
            CultureInfo.CurrentCulture = new CultureInfo("pl-PL");

            var args = ClaudeArgumentBuilder.Build(
                GenerationStage.Outline, new StageContext("marrowfield", "base"), 0.35m);

            Assert.Equal("0.35", ValueAfter(args, "--max-budget-usd"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void FeedbackNote_IsIncludedInPrompt()
    {
        var context = new StageContext(
            "marrowfield", "kid1", SceneId: "s2", FeedbackNote: "too grim, lighten the ending");

        var prompt = ValueAfter(
            ClaudeArgumentBuilder.Build(GenerationStage.Regen, context, 0.50m), "-p");

        Assert.Equal(
            "Stage: regen. Story: library/stories/marrowfield. Variant: kid1. Scene: s2. " +
            "Feedback: too grim, lighten the ending. Follow skills/story/SKILL.md.",
            prompt);
    }

    [Fact]
    public void AllowedTools_ScopeWritesToTheStorySlug()
    {
        var args = ClaudeArgumentBuilder.Build(
            GenerationStage.Extract, new StageContext("some-other-slug", "base"), 0.50m);

        Assert.Equal(
            "Read,Write(library/stories/some-other-slug/**)",
            ValueAfter(args, "--allowedTools"));
    }
}
