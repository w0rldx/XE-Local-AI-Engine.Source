namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The save interceptor encrypts exactly the columns the materialization interceptor decrypts, and no bulk update writes one.</summary>
/// <remarks>
///     Both interceptors list their columns by hand, one block per entity type, so a column added to one list only
///     reads back as ciphertext or lands on disk as plaintext. <c>ExecuteUpdate</c>'s <c>SetProperty</c> bypasses the
///     save interceptor, so it would write plaintext into an encrypted column. The scan reads comment- and
///     literal-stripped source of <c>XE-Local-AI-Engine.Client.Persistence</c>.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed partial class EncryptedColumnRegistryTests
{
    /// <summary>Non-vacuity floor under today's count: a parser that stops matching would otherwise compare two empty sets.</summary>
    private const int EncryptedColumnFloor = 125;

    private const int DbSetFloor = 50;

    private const string PersistenceRoot = "XE-Local-AI-Engine.Client.Persistence/";

    private static readonly string SaveInterceptorPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Client.Persistence", "NodeEncryptionSaveChangesInterceptor.cs");

    private static readonly string MaterializationInterceptorPath =
        RepositoryPaths.Combine("XE-Local-AI-Engine.Client.Persistence", "NodeEncryptionMaterializationInterceptor.cs");

    [Test]
    public void Interceptors_EncryptAndDecryptTheSameColumns()
    {
        var encrypted = EncryptedColumns();
        var decrypted = DecryptedColumns();

        AssertEx.True(encrypted.Count >= EncryptedColumnFloor,
            $"Parsed {encrypted.Count} encrypted columns from NodeEncryptionSaveChangesInterceptor, below the floor of "
            + $"{EncryptedColumnFloor}: the parser stopped matching the Encrypt*Property registrations.");
        AssertEx.True(decrypted.Count >= EncryptedColumnFloor,
            $"Parsed {decrypted.Count} decrypted columns from NodeEncryptionMaterializationInterceptor, below the floor "
            + $"of {EncryptedColumnFloor}: the parser stopped matching the DecryptColumns assignments.");

        var onlyEncrypted = encrypted.Except(decrypted).Order(StringComparer.Ordinal).ToList();
        var onlyDecrypted = decrypted.Except(encrypted).Order(StringComparer.Ordinal).ToList();

        AssertEx.True(onlyEncrypted.Count == 0 && onlyDecrypted.Count == 0,
            "NodeEncryptionSaveChangesInterceptor.EncryptTrackedPayloads and "
            + "NodeEncryptionMaterializationInterceptor.DecryptColumns must name the same (entity, property) columns. "
            + "Encrypted on save but never decrypted on read:"
            + Environment.NewLine + string.Join(Environment.NewLine, onlyEncrypted)
            + Environment.NewLine + "Decrypted on read but never encrypted on save:"
            + Environment.NewLine + string.Join(Environment.NewLine, onlyDecrypted));
    }

    /// <summary>A bulk <c>SetProperty</c> skips the save interceptor, so it must never target an encrypted column.</summary>
    /// <remarks>
    ///     The entity type comes from the <c>DbSet</c> member or <c>Set&lt;T&gt;()</c> nearest before the
    ///     <c>ExecuteUpdate</c> call in the same statement. When none is there (a query held in a variable), the
    ///     target counts as EVERY entity type with an encrypted column of that name: a false red costs a rename or a
    ///     tracked update, a false green writes plaintext to disk.
    /// </remarks>
    [Test]
    public void BulkUpdates_NeverWriteAnEncryptedColumn()
    {
        var encrypted = EncryptedColumns();
        var scan = ScanSetProperties();

        AssertEx.True(scan.DbSets.Count >= DbSetFloor,
            $"Found {scan.DbSets.Count} DbSet declarations under {PersistenceRoot}: the receiver map is broken.");
        AssertEx.True(scan.Targets.Count > 0,
            $"Found no SetProperty target under {PersistenceRoot}: the scan stopped matching.");

        var offenders = new List<string>();

        foreach (var target in scan.Targets)
        {
            var hits = target.EntityType is { } entityType
                ? encrypted.Where(column => column == entityType + "." + target.Property)
                : encrypted.Where(column => column.EndsWith("." + target.Property, StringComparison.Ordinal));

            offenders.AddRange(hits.Select(column =>
                $"{target.File}: SetProperty on {column}"
                + (target.EntityType is null ? " (receiver not resolved, matched by property name)" : string.Empty)));
        }

        AssertEx.Empty(offenders,
            "ExecuteUpdate/SetProperty bypasses NodeEncryptionSaveChangesInterceptor and would write plaintext into an "
            + "encrypted column. Load the entity and save it through the change tracker instead. Targets:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The (entity, property) pairs <c>EncryptTrackedPayloads</c> registers, one <c>Entries&lt;T&gt;()</c> block per entity.</summary>
    private static HashSet<string> EncryptedColumns()
    {
        var source = SourceCommentStripper.StripCommentsAndLiterals(File.ReadAllText(SaveInterceptorPath));
        var columns = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in Blocks(source, EntriesHeader()))
        {
            foreach (Match match in EncryptRegistration().Matches(block.Text))
            {
                _ = columns.Add(block.EntityType + "." + match.Groups["property"].Value);
            }
        }

        return columns;
    }

    /// <summary>The (entity, property) pairs <c>DecryptColumns</c> assigns from a decrypt of themselves, one <c>case</c> per entity.</summary>
    private static HashSet<string> DecryptedColumns()
    {
        var source = SourceCommentStripper.StripCommentsAndLiterals(File.ReadAllText(MaterializationInterceptorPath));
        var columns = new HashSet<string>(StringComparer.Ordinal);

        foreach (var block in Blocks(source, CaseHeader()))
        {
            foreach (Match match in DecryptAssignment().Matches(block.Text))
            {
                if (match.Groups["variable"].Value == block.Variable)
                {
                    _ = columns.Add(block.EntityType + "." + match.Groups["property"].Value);
                }
            }
        }

        return columns;
    }

    /// <summary>Splits <paramref name="source" /> at each header match; a block runs to the next header.</summary>
    private static IEnumerable<EntityBlock> Blocks(string source, Regex header)
    {
        var headers = header.Matches(source);

        for (var index = 0; index < headers.Count; index++)
        {
            var start = headers[index].Index + headers[index].Length;
            var end = index + 1 < headers.Count ? headers[index + 1].Index : source.Length;
            yield return new EntityBlock
            {
                EntityType = headers[index].Groups["type"].Value,
                Variable = headers[index].Groups["variable"].Value,
                Text = source[start..end]
            };
        }
    }

    private static SetPropertyScan ScanSetProperties()
    {
        var files = EnforcedSourceFiles.All()
                                       .Where(file => file.Relative.StartsWith(PersistenceRoot, StringComparison.Ordinal))
                                       .Select(file => new StrippedFile
                                       {
                                           Relative = file.Relative,
                                           Source = SourceCommentStripper.StripCommentsAndLiterals(File.ReadAllText(file.Path))
                                       })
                                       .ToList();

        var dbSets = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            foreach (Match match in DbSetDeclaration().Matches(file.Source))
            {
                dbSets[match.Groups["name"].Value] = match.Groups["type"].Value;
            }
        }

        var targets = new List<SetPropertyTarget>();

        foreach (var file in files)
        {
            foreach (Match match in SetPropertyTargetRegex().Matches(file.Source))
            {
                targets.Add(new SetPropertyTarget
                {
                    File = file.Relative,
                    EntityType = ResolveReceiver(file.Source, match.Index, dbSets),
                    Property = match.Groups["property"].Value
                });
            }
        }

        return new SetPropertyScan { DbSets = dbSets, Targets = targets };
    }

    /// <summary>The entity type of the <c>DbSet</c> or <c>Set&lt;T&gt;()</c> nearest before the statement's <c>ExecuteUpdate</c>, or null.</summary>
    private static string? ResolveReceiver(string source, int setPropertyIndex, Dictionary<string, string> dbSets)
    {
        var executeUpdate = source.LastIndexOf("ExecuteUpdate", setPropertyIndex, StringComparison.Ordinal);

        if (executeUpdate < 0)
        {
            return null;
        }

        var statementStart = source.LastIndexOf(';', executeUpdate) + 1;
        var statement = source[statementStart..executeUpdate];
        string? resolved = null;

        foreach (Match match in ReceiverCandidate().Matches(statement))
        {
            if (match.Groups["type"].Success)
            {
                resolved = match.Groups["type"].Value;
            }
            else if (dbSets.TryGetValue(match.Groups["member"].Value, out var entityType))
            {
                resolved = entityType;
            }
        }

        return resolved;
    }

    [GeneratedRegex(@"\.Entries<(?<type>\w+)>\(\)")]
    private static partial Regex EntriesHeader();

    [GeneratedRegex(@"\bcase\s+(?<type>\w+)\s+(?<variable>\w+)\s*:")]
    private static partial Regex CaseHeader();

    [GeneratedRegex(@"\bEncrypt(?:Optional|Required)Property\(\s*(?<entry>\w+)\s*,\s*\k<entry>\.Property\(\s*(?<lambda>\w+)\s*=>\s*\k<lambda>\.(?<property>\w+)\s*\)")]
    private static partial Regex EncryptRegistration();

    // The property is assigned from a Decrypt*/Unprotect call whose first argument is that same property.
    [GeneratedRegex(@"\b(?<variable>\w+)\.(?<property>\w+)\s*=(?![=>])[^;]*?\b(?:Decrypt\w*|Unprotect)\(\s*\k<variable>\.\k<property>\b")]
    private static partial Regex DecryptAssignment();

    [GeneratedRegex(@"\bDbSet<(?<type>\w+)>\s+(?<name>\w+)")]
    private static partial Regex DbSetDeclaration();

    [GeneratedRegex(@"\bSetProperty\(\s*(?<lambda>\w+)\s*=>\s*\k<lambda>\.(?<property>\w+)\b")]
    private static partial Regex SetPropertyTargetRegex();

    [GeneratedRegex(@"\bSet<(?<type>\w+)>\(\)|\.(?<member>\w+)\b")]
    private static partial Regex ReceiverCandidate();

    /// <summary>One entity type's registrations: the header's type, its pattern variable (if any) and the text up to the next header.</summary>
    private sealed class EntityBlock
    {
        public required string EntityType { get; init; }

        public required string Variable { get; init; }

        public required string Text { get; init; }
    }

    private sealed class StrippedFile
    {
        public required string Relative { get; init; }

        public required string Source { get; init; }
    }

    /// <summary>One <c>SetProperty</c> lambda target; <see cref="EntityType" /> is null when the receiver is not resolvable.</summary>
    private sealed class SetPropertyTarget
    {
        public required string File { get; init; }

        public required string? EntityType { get; init; }

        public required string Property { get; init; }
    }

    private sealed class SetPropertyScan
    {
        public required IReadOnlyDictionary<string, string> DbSets { get; init; }

        public required IReadOnlyList<SetPropertyTarget> Targets { get; init; }
    }
}
