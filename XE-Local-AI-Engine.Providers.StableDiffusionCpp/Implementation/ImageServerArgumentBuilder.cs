namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;

/// <summary>
///     Builds the exact, ordered <c>sd-server</c> startup argument vector for one model's file-set on a loopback port,
///     and is the ONLY place sd-server startup flag names live.
/// </summary>
/// <remarks>
///     Per-generation parameters (prompt, steps, seed, cfg) are NOT here — they ride the per-job HTTP body (<see cref="SdServerJobClient" />) — so startup args carry only the resident concerns: bind
///     address, model file-set, backend and threads. Model selection follows the file-set shape: a single <see cref="ImageModelPartRole.Diffusion" /> part (SD1.5) uses <c>-m</c>, a multi-part set
///     (FLUX/SD3/Qwen-Image) uses <c>--diffusion-model</c> + <c>--vae</c> + <c>--clip_l</c> [+ <c>--clip_g</c>] + <c>--t5xxl</c> [+ <c>--llm</c> + <c>--llm_vision</c>]. Verified @
///     <c>master-742-1a13107</c>. Two Qwen-adjacent flags are deliberately NOT emitted — see docs/wiki/14-image-generation.md ("sd-server flags never emitted").
/// </remarks>
internal static class ImageServerArgumentBuilder
{
    /// <summary>
    ///     Text-encoder placement key in the <c>--backend</c> component=device string.
    /// </summary>
    /// <remarks>
    ///     Verified against the pinned <c>sd-server --help</c> output: the legacy <c>--clip-on-cpu</c> flag maps to
    ///     <c>te=cpu</c>, and the help's generic <c>clip=cpu</c> example is not the text-encoder key this pinned build
    ///     accepts.
    /// </remarks>
    internal const string TextEncoderBackendKey = "te";

    /// <summary>Builds the launch spec for <paramref name="modelName" />'s resolved <paramref name="parts" /> on <paramref name="port" />.</summary>
    internal static ImageServerLaunchSpec Build(string modelName,
        string executablePath,
        IReadOnlyList<ImageModelPart> parts,
        SdGpuBackend backend,
        int port,
        StableDiffusionRuntimeOptions options,
        int threads)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(options);

        var args = new List<string>
        {
            // Loopback bind only — image generation never leaves the node.
            "--listen-ip",
            options.ListenHost,
            "--listen-port",
            port.ToString(CultureInfo.InvariantCulture)
        };

        AppendModelArgs(args, parts);

        // Acceleration backend via sd-server component-to-device syntax (no separate gpu-index flag, per spike section 4A). A GPU build keeps the text encoder and VAE on CPU to conserve VRAM because
        // the diffusion transformer dominates the memory budget (section 4.3). The CPU floor passes the bare cpu device.
        args.Add("--backend");
        args.Add(BuildBackendSpec(backend));

        args.Add("-t");
        args.Add(threads.ToString(CultureInfo.InvariantCulture));

        // Verbose so the model-load/backend-init banner is forwarded to the app log for diagnosability.
        args.Add("-v");

        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? Environment.CurrentDirectory;
        return new ImageServerLaunchSpec
        {
            ModelName = modelName,
            ExecutablePath = executablePath,
            Arguments = args,
            Port = port,
            WorkingDirectory = workingDirectory
        };
    }

    private static void AppendModelArgs(List<string> args, IReadOnlyList<ImageModelPart> parts)
    {
        var diffusion = parts.FirstOrDefault(part => part.Role == ImageModelPartRole.Diffusion)
                        ?? throw new StableDiffusionRuntimeException("The image model has no diffusion weights and cannot be served.");

        var hasSeparateParts = parts.Any(part => part.Role != ImageModelPartRole.Diffusion);
        if (!hasSeparateParts)
        {
            // Single-file model (SD1.5): one -m argument covers diffusion + baked-in VAE/text-encoder.
            args.Add("-m");
            args.Add(diffusion.LocalPath);
            return;
        }

        // Multi-part file-set (FLUX/SD3/Qwen-Image): the diffusion transformer plus each external component by role.
        args.Add("--diffusion-model");
        args.Add(diffusion.LocalPath);

        foreach (var part in parts)
        {
            switch (part.Role)
            {
                case ImageModelPartRole.Vae:
                    args.Add("--vae");
                    args.Add(part.LocalPath);
                    break;
                case ImageModelPartRole.ClipL:
                    args.Add("--clip_l");
                    args.Add(part.LocalPath);
                    break;
                case ImageModelPartRole.ClipG:
                    args.Add("--clip_g");
                    args.Add(part.LocalPath);
                    break;
                case ImageModelPartRole.T5:
                    args.Add("--t5xxl");
                    args.Add(part.LocalPath);
                    break;
                case ImageModelPartRole.Llm:
                    // Qwen-Image's text encoder is a full language model (Qwen2.5-VL), not a CLIP/T5 encoder.
                    args.Add("--llm");
                    args.Add(part.LocalPath);
                    break;
                case ImageModelPartRole.LlmVision:
                    args.Add("--llm_vision");
                    args.Add(part.LocalPath);
                    break;
                case ImageModelPartRole.Diffusion:
                default:
                    // Diffusion already emitted; nothing else to add.
                    break;
            }
        }
    }

    /// <summary>Maps the selected backend to the sd-server <c>--backend</c> component=device string.</summary>
    internal static string BuildBackendSpec(SdGpuBackend backend)
    {
        return backend switch
        {
            SdGpuBackend.Cuda => $"diffusion=cuda0,{TextEncoderBackendKey}=cpu,vae=cpu",
            SdGpuBackend.Vulkan => $"diffusion=vulkan0,{TextEncoderBackendKey}=cpu,vae=cpu",
            _ => "cpu"
        };
    }
}
