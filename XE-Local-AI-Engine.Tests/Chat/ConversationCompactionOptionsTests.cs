namespace XE_Local_AI_Engine.Tests.Chat;

using System.ComponentModel.DataAnnotations;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ConversationCompactionOptionsTests
{
    [Test]
    public void Validation_WhenTotalBudgetEqualsExactMinimum_AcceptsConfiguration()
    {
        var minimumBudget = checked((int)ConversationSummarizer.GetMinimumRequestBudget(ConversationCompactionOptions.MaximumSummaryChars));
        var options = new ConversationCompactionOptions
        {
            MaxSummaryChars = ConversationCompactionOptions.MaximumSummaryChars,
            MaxInputCharsPerSummarizationCall = minimumBudget
        };

        AssertEx.Empty(Validate(options).Select(static result => result.ErrorMessage));
    }

    [Test]
    public void Validation_WhenTotalBudgetIsOneCharacterBelowExactMinimum_RejectsConfiguration()
    {
        var minimumBudget = checked((int)ConversationSummarizer.GetMinimumRequestBudget(ConversationCompactionOptions.MaximumSummaryChars));
        var options = new ConversationCompactionOptions
        {
            MaxSummaryChars = ConversationCompactionOptions.MaximumSummaryChars,
            MaxInputCharsPerSummarizationCall = minimumBudget - 1
        };

        var result = Validate(options).Single();
        AssertEx.Contains(result.ErrorMessage ?? string.Empty, "total request", StringComparison.OrdinalIgnoreCase);
        AssertEx.Empty(result.MemberNames);
    }

    [Test]
    public void Validation_WhenBothPropertiesAreAtTheirRangeMinimums_AcceptsConfiguration()
    {
        var options = new ConversationCompactionOptions
        {
            MaxSummaryChars = ConversationCompactionOptions.MinimumSummaryChars,
            MaxInputCharsPerSummarizationCall = ConversationCompactionOptions.MinimumInputCharsPerSummarizationCall
        };

        AssertEx.Empty(Validate(options).Select(static result => result.ErrorMessage));
    }

    [Test]
    public void Defaults_LeaveRoomForSourceContentBesideTheSystemPromptAndAFullRunningSummary()
    {
        var options = new ConversationCompactionOptions();

        AssertEx.Equal(expected: 12_000, options.MaxInputCharsPerSummarizationCall,
            "The default budget is what bounds how much source each fold can carry; at 6,000 a full running summary "
            + "left ~600 characters per fold, which multiplied the folds and the loss.");
        AssertEx.Empty(Validate(options).Select(static result => result.ErrorMessage));
        AssertEx.True(options.MaxInputCharsPerSummarizationCall
            - ConversationSummarizer.GetMinimumRequestBudget(options.MaxSummaryChars) >= 6_000,
            "The defaults must leave several thousand characters of source room per fold even with the running "
            + "summary at its cap; that margin is the reason the default was raised.");
    }

    [Test]
    [Arguments(0.05, true)]
    [Arguments(0.25, true)]
    [Arguments(0.5, true)]
    [Arguments(0.04, false)]
    [Arguments(0.51, false)]
    public void Validation_RecentVerbatimFraction_IsBoundedToFiveToFiftyPercent(double fraction, bool valid)
    {
        var options = new ConversationCompactionOptions
        {
            RecentVerbatimFraction = fraction
        };

        var errors = Validate(options).SelectMany(static result => result.MemberNames).ToList();

        AssertEx.Equal(valid ? 0 : 1, errors.Count(static name => name == nameof(ConversationCompactionOptions.RecentVerbatimFraction)));
    }

    [Test]
    public void Defaults_KeepAQuarterOfTheUsableWindowVerbatim()
    {
        AssertEx.Equal(expected: 0.25, new ConversationCompactionOptions().RecentVerbatimFraction);
    }

    private static IReadOnlyList<ValidationResult> Validate(ConversationCompactionOptions options)
    {
        var results = new List<ValidationResult>();
        _ = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
