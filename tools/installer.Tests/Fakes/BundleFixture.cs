namespace XE_Local_AI_Engine.Installer.Tests.Fakes;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
///     Materializes a minimal on-disk RC bundle in a temp directory so the driver's real bodies can be
///     exercised: the three in-distro scripts, a <c>bundle-metadata.json</c> whose <c>*ScriptSha256</c>
///     values match those scripts (computed the same way the driver verifies them), vendored ps1 stubs,
///     a manifest, and an image-tar placeholder. Implements <see cref="IDisposable" /> to clean up.
/// </summary>
internal sealed class BundleFixture : IDisposable
{
    private const string StageScriptBody = "#!/usr/bin/env bash\nread -r SRC_PATH\ncp \"$SRC_PATH\" /tmp/xe-image.tar.gz\n";
    private const string LoadScriptBody = "#!/usr/bin/env bash\ndocker load -i /tmp/xe-image.tar.gz\n";
    private const string PullScriptBody = "#!/usr/bin/env bash\nollama pull qwen3:0.6b\n";
    private const string WriteManifestScriptBody = "#!/usr/bin/env bash\ncat > \"$XDG_CONFIG_HOME/xe-host-agent/manifest.yaml\"\n";

    public BundleFixture()
    {
        BundlePath = Path.Combine(Path.GetTempPath(), "xe-bundle-" + Guid.NewGuid().ToString("N"));

        var inDistro = Path.Combine(BundlePath, "payload", "in-distro-scripts");
        var images = Path.Combine(BundlePath, "payload", "images");
        var scripts = Path.Combine(BundlePath, "payload", "scripts");
        var manifestDir = Path.Combine(BundlePath, "payload", "manifest");
        Directory.CreateDirectory(inDistro);
        Directory.CreateDirectory(images);
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(manifestDir);
        Directory.CreateDirectory(Path.Combine(BundlePath, "payload", "host-agent"));
        Directory.CreateDirectory(Path.Combine(BundlePath, "payload", "rootfs"));

        File.WriteAllText(Path.Combine(inDistro, "stage-image.sh"), StageScriptBody);
        File.WriteAllText(Path.Combine(inDistro, "load-image.sh"), LoadScriptBody);
        File.WriteAllText(Path.Combine(inDistro, "pull-model.sh"), PullScriptBody);
        File.WriteAllText(Path.Combine(inDistro, "write-manifest.sh"), WriteManifestScriptBody);
        File.WriteAllText(Path.Combine(images, "xe-node-web-server.tar.gz"), "fake-tar");
        File.WriteAllText(Path.Combine(scripts, "install-host-agent.ps1"), "param()\n");
        File.WriteAllText(Path.Combine(scripts, "uninstall-host-agent.ps1"), "param()\n");
        File.WriteAllText(
            Path.Combine(manifestDir, "managed.yaml"),
            "schemaVersion: 1\ncontainers:\n    -   name: ollama\n        image: \"ollama/ollama:0.30.5\"\n    -   name: xe-node-web-server\n        image: \"ghcr.io/c0re/xe-local-ai-engine:0.1.0\"\n");

        ExpectedImageId = "sha256:" + new string('a', 64);
        BootstrapModel = "qwen3:0.6b";

        var metadata = new
        {
            schemaVersion = 1,
            imageTag = "ghcr.io/c0re/xe-local-ai-engine:0.1.0",
            XE_EXPECTED_IMAGE_ID = ExpectedImageId,
            bootstrapModel = BootstrapModel,
            stageImageScriptSha256 = ScriptSha(StageScriptBody),
            loadImageScriptSha256 = ScriptSha(LoadScriptBody),
            pullModelScriptSha256 = ScriptSha(PullScriptBody),
            writeManifestScriptSha256 = ScriptSha(WriteManifestScriptBody),
            minimumFreeDiskBytes = 12L * 1024 * 1024 * 1024
        };

        File.WriteAllText(
            Path.Combine(BundlePath, "payload", "bundle-metadata.json"),
            JsonSerializer.Serialize(metadata));
    }

    public const string WriteManifestBody = WriteManifestScriptBody;

    public string BundlePath { get; }

    public string ExpectedImageId { get; }

    public string BootstrapModel { get; }

    /// <summary>The SHA the driver expects for a script (hex of SHA-256 over UTF-8, matching VerifyScriptHash).</summary>
    public static string ScriptSha(string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    /// <summary>Corrupt a metadata SHA so the driver's hash check must abort before any process call.</summary>
    public void TamperLoadImageSha()
    {
        var path = Path.Combine(BundlePath, "payload", "bundle-metadata.json");
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        doc["loadImageScriptSha256"] = JsonSerializer.SerializeToElement(new string('f', 64));
        File.WriteAllText(path, JsonSerializer.Serialize(doc));
    }

    /// <summary>Set XE_EXPECTED_IMAGE_ID to a malformed value so LoadImage fail-closes (code#4).</summary>
    public void TamperExpectedImageId(string value)
    {
        var path = Path.Combine(BundlePath, "payload", "bundle-metadata.json");
        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        doc["XE_EXPECTED_IMAGE_ID"] = JsonSerializer.SerializeToElement(value);
        File.WriteAllText(path, JsonSerializer.Serialize(doc));
    }

    /// <summary>Remove the in-distro manifest-delivery script to exercise the HIGH-1 fail-loud path.</summary>
    public void RemoveWriteManifestScript() =>
        File.Delete(Path.Combine(BundlePath, "payload", "in-distro-scripts", "write-manifest.sh"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(BundlePath))
            {
                Directory.Delete(BundlePath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
