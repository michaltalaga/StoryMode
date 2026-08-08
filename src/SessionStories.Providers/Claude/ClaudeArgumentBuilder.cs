using System.Globalization;
using System.Text;
using SessionStories.Core.Generation;

namespace SessionStories.Providers.Claude;

/// <summary>
/// Pure construction of the `claude -p` argument vector per docs/api.md. Arguments are
/// returned as discrete tokens for ProcessStartInfo.ArgumentList — never shell-quoted strings.
/// </summary>
public static class ClaudeArgumentBuilder
{
    public static string StageName(GenerationStage stage) => stage switch
    {
        GenerationStage.Extract => "extract",
        GenerationStage.Outline => "outline",
        GenerationStage.Scene => "scene",
        GenerationStage.Regen => "regen",
        GenerationStage.Verify => "verify",
        GenerationStage.Bible => "bible",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown generation stage."),
    };

    public static string BuildPrompt(GenerationStage stage, StageContext context)
    {
        var sb = new StringBuilder()
            .Append("Stage: ").Append(StageName(stage))
            .Append(". Story: library/stories/").Append(context.StoryId)
            .Append(". Variant: ").Append(context.Variant).Append('.');
        if (!string.IsNullOrWhiteSpace(context.SceneId))
            sb.Append(" Scene: ").Append(context.SceneId).Append('.');
        if (!string.IsNullOrWhiteSpace(context.FeedbackNote))
            sb.Append(" Feedback: ").Append(context.FeedbackNote.Trim()).Append('.');
        return sb.Append(" Follow skills/story/SKILL.md.").ToString();
    }

    public static IReadOnlyList<string> Build(GenerationStage stage, StageContext context, decimal maxBudgetUsd)
    {
        var args = new List<string>
        {
            "-p", BuildPrompt(stage, context),
            "--output-format", "stream-json",
            // --verbose is required with stream-json in print mode (CLI 2.1.170).
            "--verbose",
            "--permission-mode", "acceptEdits",
            "--allowedTools", $"Read,Write(library/stories/{context.StoryId}/**)",
            "--max-budget-usd", maxBudgetUsd.ToString(CultureInfo.InvariantCulture),
        };

        // Only sequential scene drafting resumes the outline session; regen is always fresh.
        if (stage == GenerationStage.Scene && !string.IsNullOrWhiteSpace(context.ResumeSessionId))
        {
            args.Add("--resume");
            args.Add(context.ResumeSessionId);
        }

        return args;
    }
}
