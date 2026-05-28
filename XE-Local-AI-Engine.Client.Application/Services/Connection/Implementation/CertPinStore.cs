namespace XE_Local_AI_Engine.Client.Services.Connection.Implementation;

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;

public sealed class CertPinStore : ICertPinStore, IDisposable
{
    private const char Delimiter = '|';
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<CertPinStore> _logger;
    private readonly string _pinPath;

    public CertPinStore(IOptions<WorkerNodeOptions> workerOptions, ILogger<CertPinStore> logger, string? localApplicationDataRoot = null)
    {
        ArgumentNullException.ThrowIfNull(workerOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var root = string.IsNullOrWhiteSpace(localApplicationDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localApplicationDataRoot;

        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Local application data path could not be determined for certificate pin storage.");
        }

        _pinPath = Path.Combine(root, "XE-Local-AI-Engine", "cert-pins", $"{workerOptions.Value.NodeName}.pin");
    }

    public async Task<CertificatePin?> GetPinAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadPinLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SavePinAsync(X509Certificate2 certificate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var pin = CreatePin(certificate);
        var directory = Path.GetDirectoryName(_pinPath) ?? throw new InvalidOperationException("Certificate pin directory could not be determined.");

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(_pinPath, Serialize(pin), cancellationToken).ConfigureAwait(false);
            ApplyPlatformFileSecurity();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> MatchesAsync(X509Certificate2 certificate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var existing = await GetPinAsync(cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(existing.Sha256Thumbprint),
            SHA256.HashData(certificate.RawData));
    }

    public async Task ClearPinAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_pinPath))
            {
                File.Delete(_pinPath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    private async Task<CertificatePin?> ReadPinLockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_pinPath))
        {
            return null;
        }

        try
        {
            var payload = await File.ReadAllTextAsync(_pinPath, cancellationToken).ConfigureAwait(false);
            return Deserialize(payload);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read certificate pin file from {PinPath}.", _pinPath);
            return null;
        }
    }

    private static CertificatePin CreatePin(X509Certificate2 certificate)
    {
        var thumbprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var subjectCommonName = certificate.GetNameInfo(X509NameType.SimpleName, false);

        return new CertificatePin(thumbprint,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(subjectCommonName) ? certificate.Subject : subjectCommonName);
    }

    private static string Serialize(CertificatePin pin)
    {
        return string.Join(Delimiter, pin.Sha256Thumbprint, pin.PinnedAtUtc.ToString("O", CultureInfo.InvariantCulture), pin.SubjectCommonName);
    }

    private static CertificatePin? Deserialize(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        var parts = payload.Split(Delimiter);
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var pinnedAtUtc))
        {
            return null;
        }

        try
        {
            _ = Convert.FromHexString(parts[0]);
        }
        catch (FormatException)
        {
            return null;
        }

        return new CertificatePin(parts[0], pinnedAtUtc, parts[2]);
    }

    private void ApplyPlatformFileSecurity()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(_pinPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
