namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The node model builds without the warning EF logs for a database default with no sentinel, which every host
///     start would otherwise repeat for each such column.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeChatModelValidationTests
{
    [Test]
    public void Model_DeclaresASentinelForEveryDatabaseDefault()
    {
        // A private internal provider, or a model another test already validated is handed back and nothing is logged. The file is never opened.
        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"xe-model-validation-{Guid.NewGuid():N}.sqlite")}")
                      .EnableServiceProviderCaching(false)
                      .ConfigureWarnings(static warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)
                                                                    .Throw(RelationalEventId.BoolWithDefaultWarning))
                      .Options;
        using var keyHolder = new NullNodeSqliteKeyHolder();
        using var context = new NodeChatDbContext(options, keyHolder);

        var model = context.Model;

        AssertEx.NotNull(model.FindEntityType(typeof(GraphWorkflowRun)), "the run entity must be in the model this test validated.");
    }
}
