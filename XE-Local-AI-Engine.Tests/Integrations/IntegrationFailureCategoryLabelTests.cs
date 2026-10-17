namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Every failure category the node can write has an SPA label in each shipped locale, and no label names a category
///     the node never writes, so the executions list never falls back to the raw wire code.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class IntegrationFailureCategoryLabelTests
{
    [Test]
    [Arguments("en.json")]
    [Arguments("de.json")]
    public async Task EveryFailureCategory_HasExactlyOneLabelInTheLocale(string locale)
    {
        var path = RepositoryPaths.Combine("XE-Local-AI-Engine.Client.React", "src", "locales", locale);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        var labels = document.RootElement.GetProperty("pages").GetProperty("integrations").GetProperty("executions").GetProperty("failureCategories")
                             .EnumerateObject().Select(static label => label.Name).ToHashSet(StringComparer.Ordinal);

        AssertEx.True(labels.SetEquals(IntegrationFailureCategories.All),
            $"{locale} labels [{string.Join(", ", labels.Order(StringComparer.Ordinal))}] must match IntegrationFailureCategories.All [{string.Join(", ", IntegrationFailureCategories.All.Order(StringComparer.Ordinal))}].");
    }
}
