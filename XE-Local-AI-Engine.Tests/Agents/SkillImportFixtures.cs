namespace XE_Local_AI_Engine.Tests.Agents;

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;

/// <summary>
///     Archive and service fixtures for the skill-import guard suite. Everything here builds bytes in memory — no test
///     in this area touches the network or the filesystem.
/// </summary>
internal static class SkillImportFixtures
{
    /// <summary>A minimal, valid SKILL.md body used wherever the frontmatter itself is not what is under test.</summary>
    public static string SkillMarkdown(string name, string description = "Does a useful thing.")
    {
        return $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\n\nDo the thing.\n";
    }

    public static byte[] Zip(Action<ZipArchive> configure)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            configure(zip);
        }

        return buffer.ToArray();
    }

    public static void AddText(this ZipArchive zip, string name, string content)
    {
        AddBytes(zip, name, Encoding.UTF8.GetBytes(content));
    }

    public static void AddBytes(this ZipArchive zip, string name, byte[] content, int externalAttributes = 0)
    {
        var entry = zip.CreateEntry(name);
        entry.ExternalAttributes = externalAttributes;
        using var stream = entry.Open();
        stream.Write(content, offset: 0, content.Length);
    }

    /// <summary>Unix mode <c>0xA1FF</c> (<c>S_IFLNK | 0777</c>) in the high 16 bits — how a real archive marks a symlink.</summary>
    public static void AddSymlink(this ZipArchive zip, string name, string target)
    {
        AddBytes(zip, name, Encoding.UTF8.GetBytes(target), externalAttributes: unchecked((int)0xA1FF0000));
    }

    /// <summary>
    ///     Rewrites every declared uncompressed size in <paramref name="archive" /> to <paramref name="declared" />,
    ///     leaving the compressed payload untouched — the central-directory and local-header sizes are attacker-authored
    ///     assertions, not measurements.
    ///     <para>
    ///         Overstating is the exploitable direction: an implementation that allocated <c>new byte[entry.Length]</c>
    ///         or refused on that number can be steered by a header a harmless archive simply lies in. Understating is
    ///         not constructible through <see cref="ZipArchive" /> — its read path stops inflating at the declared size,
    ///         so a 2 MiB payload declared as 4096 yields exactly 4096 bytes.
    ///     </para>
    /// </summary>
    public static byte[] LieAboutSizes(byte[] archive, uint declared)
    {
        var copy = (byte[])archive.Clone();
        var eocd = FindEndOfCentralDirectory(copy);
        var count = BitConverter.ToUInt16(copy, eocd + 10);
        var cursor = (int)BitConverter.ToUInt32(copy, eocd + 16);

        for (var index = 0; index < count; index++)
        {
            var nameLength = BitConverter.ToUInt16(copy, cursor + 28);
            var extraLength = BitConverter.ToUInt16(copy, cursor + 30);
            var commentLength = BitConverter.ToUInt16(copy, cursor + 32);
            var localHeader = (int)BitConverter.ToUInt32(copy, cursor + 42);

            BitConverter.GetBytes(declared).CopyTo(copy, cursor + 24);
            BitConverter.GetBytes(declared).CopyTo(copy, localHeader + 22);

            cursor += 46 + nameLength + extraLength + commentLength;
        }

        return copy;
    }

    /// <summary>What <see cref="ZipArchiveEntry.Length" /> reports — i.e. what a naive implementation would have trusted.</summary>
    public static long DeclaredLength(byte[] archive, string entryName)
    {
        using var stream = new MemoryStream(archive, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return zip.GetEntry(entryName)!.Length;
    }

    /// <summary>Text that does not compress far, so a size-bound test cannot be satisfied by the ratio guard instead.</summary>
    public static string IncompressibleText(int length, int seed)
    {
        var random = new Random(seed);
        var builder = new StringBuilder(length);
        for (var index = 0; index < length; index++)
        {
            builder.Append((char)('a' + random.Next(maxValue: 26)));
        }

        return builder.ToString();
    }

    private static int FindEndOfCentralDirectory(byte[] archive)
    {
        for (var index = archive.Length - 22; index >= 0; index--)
        {
            if (BitConverter.ToUInt32(archive, index) == 0x06054b50)
            {
                return index;
            }
        }

        throw new InvalidOperationException("The fixture archive has no end-of-central-directory record.");
    }
}

/// <summary>
///     One import service wired to a substituted store, an isolated cache, and a handler that fails loudly on any
///     unexpected HTTP call. Owns the disposables so each test is a single <c>using</c>.
/// </summary>
internal sealed class SkillImportHarness : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly HttpClient _httpClient;

    /// <summary>
    ///     <paramref name="options" /> lets a cap test tighten a limit so the fixture stays small. The shipped defaults
    ///     are asserted separately, in <c>SkillImportOptions_DefaultsMatchTheRuledLimits</c> — a test that builds a
    ///     50 MiB archive proves nothing a tightened one does not, and costs a second every run.
    /// </summary>
    public SkillImportHarness(QueuedHttpMessageHandler? handler = null, SkillImportOptions? options = null)
    {
        Handler = handler ?? new QueuedHttpMessageHandler();
        _httpClient = new HttpClient(Handler);
        Store = CreateStore();
        Service = new SkillImportService(Store, _cache, TimeProvider.System, _httpClient, options ?? new SkillImportOptions());
    }

    public QueuedHttpMessageHandler Handler { get; }

    public IAgentSkillStore Store { get; }

    public SkillImportService Service { get; }

    public void Dispose()
    {
        _httpClient.Dispose();
        Handler.Dispose();
        _cache.Dispose();
    }

    /// <summary>Makes the library already hold <paramref name="names" />, so conflict handling can be exercised.</summary>
    public void SeedExistingSkills(params string[] names)
    {
        Store.ListAsync(Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<AgentSkillRecord>>(names
                                                                       .Select(static name => new AgentSkillRecord(Guid.NewGuid(), name, "old", "old body", Enabled: true, Version: 3, CreatedAtUtc: 1,
                                                                           UpdatedAtUtc: 1))
                                                                       .ToList()));
    }

    /// <summary>The single <c>AgentSkillInput</c> handed to the store, whichever write path took it.</summary>
    public AgentSkillInput WrittenInput(string methodName)
    {
        var call = Store.ReceivedCalls().Single(received => string.Equals(received.GetMethodInfo().Name, methodName, StringComparison.Ordinal));
        return call.GetArguments().OfType<AgentSkillInput>().Single();
    }

    private static IAgentSkillStore CreateStore()
    {
        var store = Substitute.For<IAgentSkillStore>();
        store.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AgentSkillRecord>>([]));
        store.CreateAsync(Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>())
             .Returns(call => Task.FromResult(Record(call.Arg<AgentSkillInput>(), Guid.NewGuid())));
        store.UpdateAsync(Arg.Any<Guid>(), Arg.Any<AgentSkillInput>(), Arg.Any<CancellationToken>())
             .Returns(call => Task.FromResult<AgentSkillRecord?>(Record(call.Arg<AgentSkillInput>(), call.Arg<Guid>())));
        return store;
    }

    private static AgentSkillRecord Record(AgentSkillInput input, Guid id)
    {
        return new AgentSkillRecord(id,
            input.Name,
            input.Description,
            input.Body,
            input.Enabled,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10,
            input.License,
            input.Compatibility,
            input.AllowedTools,
            input.Metadata,
            input.Origin,
            input.SourceUri,
            input.ImportedAtUtc,
            input.ContentSha256);
    }
}

/// <summary>
///     Replays queued responses in order and records every URI requested, so redirect hops are assertable. An empty
///     queue answers 404, which makes an unexpected request a visible failure rather than a hang. The queued responses
///     are owned by this handler and disposed with it.
/// </summary>
internal sealed class QueuedHttpMessageHandler : HttpMessageHandler
{
    // Response factories rather than instances: the message is built inside SendAsync and handed straight to the
    // caller, which owns its disposal. Queuing live instances would leave undisposed responses for any test whose
    // queue is not drained.
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<Uri> RequestedUris { get; } = [];

    public QueuedHttpMessageHandler EnqueueRedirect(string location)
    {
        _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers =
            {
                Location = new Uri(location)
            }
        });
        return this;
    }

    public QueuedHttpMessageHandler EnqueueArchive(byte[] archive)
    {
        _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive)
            {
                Headers =
                {
                    ContentType = new MediaTypeHeaderValue("application/zip")
                }
            }
        });
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestedUris.Add(request.RequestUri!);
        var factory = _responses.Count > 0 ? _responses.Dequeue() : NotFound;
        return Task.FromResult(factory());
    }

    /// <summary>An empty queue answers 404, so an unexpected request fails the test visibly instead of hanging.</summary>
    private static HttpResponseMessage NotFound()
    {
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
