namespace XE_Local_AI_Engine.Tests.Chat;

using System.ComponentModel.DataAnnotations;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Tests.Testing;

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

    private static IReadOnlyList<ValidationResult> Validate(ConversationCompactionOptions options)
    {
        var results = new List<ValidationResult>();
        _ = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
