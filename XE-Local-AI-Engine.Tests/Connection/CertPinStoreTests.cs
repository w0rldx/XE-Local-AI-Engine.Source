namespace XE_Local_AI_Engine.Tests.Connection;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Connection.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class CertPinStoreTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Test]
    public async Task SavePinAsync_WhenCertificateProvided_PersistsPin()
    {
        using var store = CreateStore();
        using var certificate = CreateCertificate("worker-node-test");

        await store.SavePinAsync(certificate);
        var pin = AssertEx.NotNull(await store.GetPinAsync());

        AssertEx.Equal(Convert.ToHexString(SHA256.HashData(certificate.RawData)), pin.Sha256Thumbprint);
        AssertEx.Equal("worker-node-test", pin.SubjectCommonName);
    }

    [Test]
    public async Task MatchesAsync_WhenStoredCertificateMatches_ReturnsTrue()
    {
        using var store = CreateStore();
        using var certificate = CreateCertificate("worker-node-test");

        await store.SavePinAsync(certificate);

        AssertEx.True(await store.MatchesAsync(certificate));
    }

    [Test]
    public async Task GetPinAsync_WhenFileIsCorrupted_ReturnsNull()
    {
        using var store = CreateStore();
        var pinPath = Path.Combine(_rootPath, "XE-Local-AI-Engine", "cert-pins", "worker-node-test.pin");
        Directory.CreateDirectory(Path.GetDirectoryName(pinPath)!);
        await File.WriteAllTextAsync(pinPath, "broken");

        AssertEx.Null(await store.GetPinAsync());
    }

    [Test]
    public async Task ClearPinAsync_WhenPinExists_RemovesPin()
    {
        using var store = CreateStore();
        using var certificate = CreateCertificate("worker-node-test");

        await store.SavePinAsync(certificate);
        await store.ClearPinAsync();

        AssertEx.Null(await store.GetPinAsync());
    }

    private CertPinStore CreateStore()
    {
        Directory.CreateDirectory(_rootPath);

        return new CertPinStore(Options.Create(new WorkerNodeOptions
        {
            NodeName = "worker-node-test"
        }), NullLogger<CertPinStore>.Instance, _rootPath);
    }

    private static X509Certificate2 CreateCertificate(string subjectCommonName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subjectCommonName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
    }
}
