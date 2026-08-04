namespace XE_Local_AI_Engine.Tests.Images;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Images.Catalog;
using XE_Local_AI_Engine.Client.Services.Images.Catalog.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The curated image-model catalog is the surface that replaces hand-typing a repo id, a weight file name, a model
///     name and a family. That only holds if every shipped row is installable, so these tests cover both halves: the
///     validator refuses the specific shapes that would produce a broken one-click install, and the bundled seed the
///     app actually ships passes that validator with the multi-repo Qwen-Image set intact.
/// </summary>
public sealed class ImageModelCatalogTests
{
    [Test]
    public void BundledCatalog_LoadsAndValidates_WithEveryEntryInstallable()
    {
        var catalog = new ImageModelCatalog(NullLogger<ImageModelCatalog>.Instance);

        var document = catalog.GetDocument();

        AssertEx.Equal(ImageModelCatalogValidator.SupportedSchemaVersion, document.SchemaVersion);
        AssertEx.NotEmpty(document.Models, "An empty catalog means the embedded resource failed to load or validate — the loader degrades silently.");

        foreach (var entry in document.Models)
        {
            AssertEx.True(entry.Parts.Any(static part => string.Equals(part.Role, "Diffusion", StringComparison.Ordinal)),
                $"Catalog entry '{entry.Id}' has no diffusion part and cannot be served.");
            foreach (var part in entry.Parts)
            {
                // A missing size is not cosmetic: it turns the free-disk pre-flight into a no-op and makes the set
                // percentage incomputable, which on an 18 GB install is the difference between a usable bar and none.
                AssertEx.True(part.SizeBytes > 0, $"Catalog entry '{entry.Id}' has a part with no declared size.");
            }
        }
    }

    [Test]
    public void BundledCatalog_ShipsTheSd15EntryUnderTheNameTheInstallRegistersItAs()
    {
        // The catalog id doubles as the model name the download is registered under, which is what lets the panel show
        // an "Installed" state by matching ids against the registry. A drift here silently breaks that badge.
        var document = new ImageModelCatalog(NullLogger<ImageModelCatalog>.Instance).GetDocument();

        var sd15 = document.Models.Single(static entry => entry.Id == "sd-1.5");
        AssertEx.Equal("second-state/stable-diffusion-v1-5-GGUF", sd15.RepoId);
        AssertEx.Equal("Sd15", sd15.Family);
        AssertEx.Equal(expected: 1, sd15.Parts.Count, "SD 1.5 is a single-file model.");
    }

    [Test]
    public void BundledCatalog_QwenImageEntry_SpansTwoRepositories()
    {
        // The reason ImageModelPartRequest carries a per-part RepoId at all: the Qwen-Image diffusion transformer and
        // VAE ship in one repository while the Qwen2.5-VL text encoder ships in another. A catalog entry that assumed
        // one repo could not install this model at all.
        var document = new ImageModelCatalog(NullLogger<ImageModelCatalog>.Instance).GetDocument();

        var qwen = document.Models.Single(static entry => entry.Id == "qwen-image");
        var encoder = qwen.Parts.Single(static part => string.Equals(part.Role, "Llm", StringComparison.Ordinal));

        AssertEx.NotNull(encoder.RepoId);
        AssertEx.NotEqual(qwen.RepoId, encoder.RepoId!);
        AssertEx.Null(qwen.Parts.Single(static part => string.Equals(part.Role, "Diffusion", StringComparison.Ordinal)).RepoId,
            "A part in the set's own repo must leave repoId null rather than repeat it.");
    }

    [Test]
    public void Validator_RejectsAPartWhoseFileNameEscapesTheModelsDirectory()
    {
        // The catalog is in-repo content, but it feeds the same download path an untrusted repo listing feeds. It goes
        // through the same containment guard rather than being trusted for being ours.
        var result = ImageModelCatalogValidator.Validate(CatalogWithPart("""
                                                                        { "role": "Diffusion", "fileName": "../../../etc/pwned.gguf", "sizeBytes": 10 }
                                                                        """));

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(static error => error.Contains("safe repo-relative path", StringComparison.Ordinal)));
    }

    [Test]
    public void Validator_RejectsAPartWithNoDeclaredSize()
    {
        var result = ImageModelCatalogValidator.Validate(CatalogWithPart("""
                                                                        { "role": "Diffusion", "fileName": "weights.gguf", "sizeBytes": 0 }
                                                                        """));

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(static error => error.Contains("sizeBytes must be positive", StringComparison.Ordinal)));
    }

    [Test]
    public void Validator_RejectsASetWithNoDiffusionPart()
    {
        var result = ImageModelCatalogValidator.Validate(CatalogWithPart("""
                                                                        { "role": "Vae", "fileName": "vae.safetensors", "sizeBytes": 10 }
                                                                        """));

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(static error => error.Contains("Diffusion part", StringComparison.Ordinal)));
    }

    [Test]
    public void Validator_RejectsADuplicateRole()
    {
        // The launch argument builder emits one flag per role, so a second VAE would be silently dropped at run time —
        // the operator would pay for the download and never use the file.
        var result = ImageModelCatalogValidator.Validate(CatalogWithPart("""
                                                                        { "role": "Diffusion", "fileName": "a.gguf", "sizeBytes": 10 },
                                                                        { "role": "Vae", "fileName": "vae1.safetensors", "sizeBytes": 10 },
                                                                        { "role": "Vae", "fileName": "vae2.safetensors", "sizeBytes": 10 }
                                                                        """));

        AssertEx.False(result.IsValid);
        AssertEx.True(result.Errors.Any(static error => error.Contains("more than once", StringComparison.Ordinal)));
    }

    [Test]
    public void Validator_RejectsAnUnknownFamily()
    {
        var raw = """
                  {
                    "schemaVersion": 1,
                    "catalogVersion": "test",
                    "models": [
                      {
                        "id": "x", "displayName": "X", "publisher": "p", "repoId": "o/r",
                        "family": "NotAFamily", "license": "mit", "recommended": false,
                        "parts": [ { "role": "Diffusion", "fileName": "a.gguf", "sizeBytes": 10 } ]
                      }
                    ]
                  }
                  """;

        var result = ImageModelCatalogValidator.Validate(raw);

        AssertEx.False(result.IsValid);
    }

    [Test]
    public void Validator_RejectsMalformedJson_WithoutThrowing()
    {
        var result = ImageModelCatalogValidator.Validate("{ not json");

        AssertEx.False(result.IsValid);
        AssertEx.NotEmpty(result.Errors);
    }

    [Test]
    public void Validator_AcceptsAWellFormedCrossRepoSet()
    {
        var raw = """
                  {
                    "schemaVersion": 1,
                    "catalogVersion": "test",
                    "models": [
                      {
                        "id": "qwen", "displayName": "Qwen", "publisher": "QuantStack", "repoId": "QuantStack/Qwen-Image-GGUF",
                        "family": "QwenImage", "license": "apache-2.0", "recommended": false,
                        "parts": [
                          { "role": "Diffusion", "fileName": "Qwen_Image-Q4_K_M.gguf", "sizeBytes": 13065746976 },
                          { "role": "Vae", "fileName": "VAE/Qwen_Image-VAE.safetensors", "sizeBytes": 253806246 },
                          { "role": "Llm", "fileName": "enc.gguf", "repoId": "mradermacher/Qwen2.5-VL-7B-Instruct-GGUF", "sizeBytes": 4683072512 }
                        ]
                      }
                    ]
                  }
                  """;

        var result = ImageModelCatalogValidator.Validate(raw);

        AssertEx.True(result.IsValid, result.Errors.Count > 0 ? result.Errors[0] : "expected a valid catalog");
        AssertEx.Equal(ImageModelPartRole.Llm.ToString(), result.Document!.Models[0].Parts[2].Role);
    }

    private static string CatalogWithPart(string partsJson)
    {
        return $$"""
                 {
                   "schemaVersion": 1,
                   "catalogVersion": "test",
                   "models": [
                     {
                       "id": "x", "displayName": "X", "publisher": "p", "repoId": "o/r",
                       "family": "Sd15", "license": "mit", "recommended": false,
                       "parts": [ {{partsJson}} ]
                     }
                   ]
                 }
                 """;
    }
}
