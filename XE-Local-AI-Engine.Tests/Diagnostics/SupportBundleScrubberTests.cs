namespace XE_Local_AI_Engine.Tests.Diagnostics;

using XE_Local_AI_Engine.Client.Services.Diagnostics;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The support bundle's privacy boundary: each scrubbed shape, the shapes deliberately kept so a bundle stays
///     useful (trace ids, model names, log categories, URLs), idempotence and a pathological line.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SupportBundleScrubberTests
{
    private static readonly SupportBundleScrubber Posix = new(["/home/jane"], "/srv/xe-data", ignoreCase: false);
    private static readonly SupportBundleScrubber Windows = new([@"C:\Users\Jane"], @"D:\XE\data", ignoreCase: true);

    [Test]
    [Arguments("/home/jane/models/a.gguf loaded", "~/models/a.gguf loaded")]
    [Arguments("cwd=/home/jane", "cwd=~")]
    [Arguments("/srv/xe-data/logs/xe-node-20261002.log", "<data>/logs/xe-node-20261002.log")]
    [Arguments("/home/janet/notes.txt", "notes.txt")]
    [Arguments("spawned /opt/llama/bin/llama-server --port 1", "spawned llama-server --port 1")]
    public void Posix_MapsHomeAndDataRoot_AndCutsOtherPathsToTheirLeaf(string input, string expected)
    {
        AssertEx.Equal(expected, Posix.Scrub(input));
    }

    [Test]
    [Arguments(@"c:\users\jane\AppData\Local\x.log", @"~\AppData\Local\x.log")]
    [Arguments("C:/Users/Jane/Downloads/m.gguf", "~/Downloads/m.gguf")]
    [Arguments(@"D:\XE\data\logs\desktop.log", @"<data>\logs\desktop.log")]
    [Arguments(@"E:\models\big\a.gguf ok", "a.gguf ok")]
    public void Windows_MapsPrefixesCaseInsensitively_InEitherSlashStyle(string input, string expected)
    {
        AssertEx.Equal(expected, Windows.Scrub(input));
    }

    [Test]
    public void CaseSensitiveHost_DoesNotTreatADifferentlyCasedPathAsHome()
    {
        var scrubber = new SupportBundleScrubber([@"C:\Users\Jane"], dataRoot: null, ignoreCase: false);

        AssertEx.Equal("x.log", scrubber.Scrub(@"c:\users\jane\x.log"));
    }

    [Test]
    public void DataRootUnderHome_WinsOverTheHomePrefix()
    {
        var scrubber = new SupportBundleScrubber(["/home/jane"], "/home/jane/.local/share/XE", ignoreCase: false);

        AssertEx.Equal("<data>/logs/a.log and ~/b", scrubber.Scrub("/home/jane/.local/share/XE/logs/a.log and /home/jane/b"));
    }

    [Test]
    [Arguments("mail jane.doe+x@example.co.uk now", "mail [redacted-email] now")]
    [Arguments("Authorization: Bearer abc.DEF-123_xyz", "Authorization: Bearer [redacted-token]")]
    [Arguments("key sk-proj-abcdefghij1234567890XYZ end", "key [redacted-token] end")]
    [Arguments("ghp_abcdefghijklmnopqrstuvwxyz0123456789 end", "[redacted-token] end")]
    [Arguments("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV", "token [redacted-token]")]
    [Arguments("hf: hf_AbCdEfGhIjKlMnOpQrStUvWxYz123456", "hf: [redacted-token]")]
    [Arguments("token=AbCdEfGh12345678IjKl", "[redacted-token]")]
    [Arguments("api AbCdEfGh12345678IjKlMnOp end", "api [redacted-token] end")]
    [Arguments("key Zq7mXr4pLw9vKt2nHy3b end", "key [redacted-token] end")]
    [Arguments("HF_TOKEN hf_AbCdEfGhIjKlMnOpQrStUvWxYz end", "HF_TOKEN [redacted-token] end")]
    [Arguments("aws AKIAIOSFODNN7EXAMPLE end", "aws [redacted-token] end")]
    [Arguments("slack xoxb-abcdefghijkl-mnop end", "slack [redacted-token] end")]
    [Arguments("google AIzaSyAbCdEfGhIjKlMnOpQrStUvWxYzAbCdEfG end", "google [redacted-token] end")]
    [Arguments("conn AccountKey=AbCdEfGhIjKlMnOpQrStUv== end", "conn [redacted-token] end")]
    public void SecretShapes_AreMasked(string input, string expected)
    {
        AssertEx.Equal(expected, Posix.Scrub(input));
    }

    [Test]
    [Arguments("[trace:00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01]")]
    [Arguments("id 3f2504e0-4f89-41d3-9a0c-0305e82c3301 and 3f2504e04f8941d39a0c0305e82c3301")]
    [Arguments("loaded Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf and Meta-Llama-3.1-8B-Instruct")]
    [Arguments("XE_Local_AI_Engine.Providers.LlamaServer.Implementation.LlamaServerProcessLauncher: started")]
    [Arguments("see https://github.com/w0rldx/XE-Local-AI-Engine.Source/issues/new")]
    [Arguments("2026-10-02 10:14:00.123 +02:00 [INF] 1696234567890123456789 tokens")]
    public void DiagnosticShapes_AreKept(string input)
    {
        AssertEx.Equal(input, Posix.Scrub(input));
    }

    [Test]
    [Arguments("Container bridge listens on 192.168.178.20:18790", "Container bridge listens on [redacted-ip]:18790")]
    [Arguments("peer 10.0.0.7 refused", "peer [redacted-ip] refused")]
    [Arguments("http://172.16.4.2:8080/v1 and 172.31.255.1.", "http://[redacted-ip]:8080/v1 and [redacted-ip].")]
    [Arguments("link-local 169.254.12.3", "link-local [redacted-ip]")]
    public void PrivateIpv4_IsRedacted_AndThePortKept(string input, string expected)
    {
        AssertEx.Equal(expected, Posix.Scrub(input));
        AssertEx.Equal(expected, Posix.Scrub(expected), "A second pass changes nothing.");
    }

    [Test]
    [Arguments("listening on http://127.0.0.1:5123")]
    [Arguments("public 8.8.8.8 and 172.32.0.1 and 172.15.0.1 and 192.169.0.1")]
    [Arguments("Windows 10.0.19041.1 build and llama.cpp 1.10.0.1.2")]
    public void LoopbackPublicAddressesAndVersions_AreKept(string input)
    {
        AssertEx.Equal(input, Posix.Scrub(input));
    }

    [Test]
    public void TraceIdsAndCommitShas_Survive_WhileSecretsOnTheSameLineAreReplaced()
    {
        const string trace = "[trace:4bf92f3577b34da6a3ce929d0e0e4736 span:00f067aa0ba902b7]";
        const string sha = "commit 9fceb02d0ae598e95dc970b74767f19372d61af8";
        const string secrets = "sk-abc123def456ghi789jkl ghp_abcdefghijklmnopqrstuvwxyz0123456789 "
                               + "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV jane@example.com";

        AssertEx.Equal(trace, Posix.Scrub(trace));
        AssertEx.Equal(sha, Posix.Scrub(sha));
        AssertEx.Equal("[redacted-token] [redacted-token] [redacted-token] [redacted-email]", Posix.Scrub(secrets));
    }

    [Test]
    public void DateStampedFileUnderTheDataRoot_KeepsItsPath()
    {
        var scrubber = new SupportBundleScrubber(["/home/jane"], "/home/jane/projects/XE-Local-AI-Engine.Source/XE-Local-AI-Engine.Client", ignoreCase: false);
        const string line = "Snapshotted the node database to /home/jane/projects/XE-Local-AI-Engine.Source/XE-Local-AI-Engine.Client/backups/"
                            + "node-chat-20260913T134250067Z.sqlite (4096 bytes) before applying 96 migrations";

        AssertEx.Equal("Snapshotted the node database to <data>/backups/node-chat-20260913T134250067Z.sqlite (4096 bytes) before applying 96 migrations",
            scrubber.Scrub(line));
    }

    [Test]
    [Arguments("XE Local AI Engine 1.0.0-rc.2+428560129c4e887f686ba07fe66f021a9afd07fc starting")]
    [Arguments("at 2026-10-02T09:16:29.1234567Z and 20261002T091629Z and 2026-10-02T09:16:29+02:00")]
    [Arguments("~/backups/node-chat-20261002T091629-2026100209162912.sqlite")]
    [Arguments("Applying migration '20260913134250_AddBenchmarkP2Discrimination' from migration 'AddBenchmarkP2Discrimination'")]
    public void VersionsAndTimestamps_InLogLines_AreKept(string line)
    {
        AssertEx.Equal(line, Posix.Scrub(line));
    }

    [Test]
    [Arguments("password=hunter22 api_key: plainkey", "password=[redacted-token] api_key: [redacted-token]")]
    [Arguments("password=\"hunter22\"", "password=\"[redacted-token]\"")]
    [Arguments("password: 'hunter22'", "password: '[redacted-token]'")]
    [Arguments("{\"password\":\"hunter22\"}", "{\"password\":\"[redacted-token]\"}")]
    [Arguments("{\"api_key\": \"sk-short\", \"Client_Secret\": \"it's a secret\", \"name\": \"kept\"}",
        "{\"api_key\": \"[redacted-token]\", \"Client_Secret\": \"[redacted-token]\", \"name\": \"kept\"}")]
    public void SecretAssignments_MaskTheValue_KeepingNameAndQuotes(string input, string expected)
    {
        var once = Posix.Scrub(input);

        AssertEx.Equal(expected, once);
        AssertEx.Equal(once, Posix.Scrub(once), "A masked assignment must not change on a second pass.");
    }

    [Test]
    public void Scrub_IsIdempotent()
    {
        const string sample = "/home/jane/x/y.log\n/srv/xe-data/logs/a.log\r\njane@example.com Bearer abcdefghijkl1234\n"
                              + @"C:\Users\Jane\AppData\z.txt sk-abcdefghijklmnop1234" + "\n~/already/scrubbed <data>/logs/b.log";

        var once = Posix.Scrub(sample);
        var twice = Posix.Scrub(once);

        AssertEx.Equal(once, twice);
        AssertEx.Equal(Windows.Scrub(Windows.Scrub(sample)), Windows.Scrub(sample));
        AssertEx.True(once.Contains("~/already/scrubbed", StringComparison.Ordinal), once);
        AssertEx.True(once.Contains("<data>/logs/b.log", StringComparison.Ordinal), once);
        AssertEx.Equal(expected: 5, once.Split('\n').Length, "Scrubbing keeps the line structure.");
    }

    [Test]
    public void PathologicalLine_IsEitherKeptVerbatimOrDropped_AndNeverBreaksItsNeighbours()
    {
        // No e-mail, path or token in it, so the only correct outputs are the line itself or the timeout marker.
        var pathological = new string('a', 50_000) + "@" + new string('b', 50_000);

        var result = Posix.Scrub("before jane@example.com\n" + pathological + "\nafter");
        var lines = result.Split('\n');

        AssertEx.Equal("before [redacted-email]", lines[0]);
        AssertEx.True(lines[1] == pathological || lines[1] == SupportBundleScrubber.DroppedLine, "The pathological line must not be half-scrubbed.");
        AssertEx.Equal("after", lines[2]);
    }

    [Test]
    [Arguments("/")]
    [Arguments("/home")]
    [Arguments("C:")]
    [Arguments("")]
    public void UnusablePrefix_IsIgnored(string root)
    {
        var scrubber = new SupportBundleScrubber([root], root, ignoreCase: false);

        AssertEx.Equal("a.log", scrubber.Scrub("/home/jane/a.log"));
    }
}
