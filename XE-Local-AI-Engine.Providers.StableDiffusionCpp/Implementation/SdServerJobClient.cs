namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     The status of an sd-server async job, mapped from the <c>GET /sdcpp/v1/jobs/{id}</c> response (frozen spike §4A).
/// </summary>
internal enum SdJobStatus
{
    /// <summary>Accepted, waiting for a generation slot.</summary>
    Queued,

    /// <summary>Actively generating.</summary>
    Generating,

    /// <summary>Finished; the decoded image is available on the state.</summary>
    Completed,

    /// <summary>Failed; a sanitized error message is available on the state.</summary>
    Failed,

    /// <summary>Cancelled by a prior cancel request.</summary>
    Cancelled,

    /// <summary><c>410 Gone</c> — the job's 600s result TTL elapsed and the record was purged.</summary>
    Expired,

    /// <summary><c>404</c> — the server does not know this job id (should not happen for a job we submitted).</summary>
    Unknown
}

/// <summary>The outcome of a <c>POST /sdcpp/v1/jobs/{id}/cancel</c> call.</summary>
internal enum SdCancelOutcome
{
    /// <summary><c>200</c> — the job was queued (now cancelled) or already terminal (idempotent).</summary>
    Cancelled,

    /// <summary><c>409 Conflict</c> — the job is generating and cannot be interrupted over HTTP; abort by tree-kill + restart.</summary>
    Generating,

    /// <summary><c>404</c>/<c>410</c> — the server no longer tracks this job; nothing to cancel.</summary>
    NotFoundOrGone
}

/// <summary>A resolved job-poll observation: status plus any completed-image / error payload.</summary>
internal sealed record SdJobState
{
    public required SdJobStatus Status { get; init; }

    /// <summary>Queue position while <see cref="SdJobStatus.Queued" />, when the server reports one.</summary>
    public int? QueuePosition { get; init; }

    /// <summary>The decoded PNG bytes when <see cref="SdJobStatus.Completed" />; otherwise <see langword="null" />.</summary>
    public byte[]? ImageBytes { get; init; }

    /// <summary>The seed the server actually used, when it reported one.</summary>
    public long? Seed { get; init; }
}

/// <summary>
///     Typed HTTP client for the sd-server native async job API (<c>/sdcpp/v1/*</c>). The ONLY place sd-server route
///     strings, request-body shape, and response JSON live (architecture invariant §3). Prompts pass through the request
///     body but are NEVER logged here (§10). Frozen field-level against stable-diffusion.cpp @ <c>master-742-1a13107</c>
///     (§4A).
/// </summary>
internal sealed class SdServerJobClient
{
    internal const string ImgGenRoute = "sdcpp/v1/img_gen";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;

    public SdServerJobClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    ///     Submits a generation job (<c>POST /sdcpp/v1/img_gen</c>) and returns the server-minted job id. Maps a full
    ///     queue (<c>429</c>) and any other non-success to a sanitized <see cref="StableDiffusionRuntimeException" />.
    /// </summary>
    public async Task<string> SubmitAsync(Uri baseAddress, ImageGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(request);

        var body = MapRequest(request);
        using var content = JsonContent.Create(body, options: JsonOptions);
        using var response = await _httpClient.PostAsync(new Uri(baseAddress, ImgGenRoute), content, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new StableDiffusionRuntimeException("The image runtime queue is full. Try again shortly.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new StableDiffusionRuntimeException("The image runtime rejected the generation request.");
        }

        var accepted = await response.Content.ReadFromJsonAsync<ImgGenAcceptedResponse>(JsonOptions, ct).ConfigureAwait(false);
        if (accepted?.Id is not { Length: > 0 } id)
        {
            throw new StableDiffusionRuntimeException("The image runtime accepted the request but returned no job id.");
        }

        return id;
    }

    /// <summary>
    ///     Polls one job (<c>GET /sdcpp/v1/jobs/{id}</c>). Maps <c>410</c>→<see cref="SdJobStatus.Expired" />,
    ///     <c>404</c>→<see cref="SdJobStatus.Unknown" />, and a <c>completed</c> body's <c>result.images[0].b64_json</c>
    ///     to decoded PNG bytes on the returned state.
    /// </summary>
    public async Task<SdJobState> GetJobAsync(Uri baseAddress, string jobId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var response = await _httpClient.GetAsync(new Uri(baseAddress, JobRoute(jobId)), ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            return new SdJobState
            {
                Status = SdJobStatus.Expired
            };
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new SdJobState
            {
                Status = SdJobStatus.Unknown
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new StableDiffusionRuntimeException("The image runtime returned an unexpected status while polling the job.");
        }

        var payload = await response.Content.ReadFromJsonAsync<JobStateResponse>(JsonOptions, ct).ConfigureAwait(false);
        return MapState(payload);
    }

    /// <summary>
    ///     Requests cancellation of one job (<c>POST /sdcpp/v1/jobs/{id}/cancel</c>). <c>200</c> (queued/terminal)→
    ///     <see cref="SdCancelOutcome.Cancelled" />, <c>409</c> (generating)→<see cref="SdCancelOutcome.Generating" />,
    ///     <c>404</c>/<c>410</c>→<see cref="SdCancelOutcome.NotFoundOrGone" />.
    /// </summary>
    public async Task<SdCancelOutcome> CancelAsync(Uri baseAddress, string jobId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var response = await _httpClient.PostAsync(new Uri(baseAddress, CancelRoute(jobId)), content: null, ct).ConfigureAwait(false);

        return response.StatusCode switch
        {
            HttpStatusCode.Conflict => SdCancelOutcome.Generating,
            HttpStatusCode.NotFound or HttpStatusCode.Gone => SdCancelOutcome.NotFoundOrGone,
            _ => SdCancelOutcome.Cancelled
        };
    }

    private static string JobRoute(string jobId)
    {
        return $"sdcpp/v1/jobs/{Uri.EscapeDataString(jobId)}";
    }

    private static string CancelRoute(string jobId)
    {
        return $"sdcpp/v1/jobs/{Uri.EscapeDataString(jobId)}/cancel";
    }

    private static ImgGenRequestBody MapRequest(ImageGenerationRequest request)
    {
        return new ImgGenRequestBody
        {
            Prompt = request.Prompt,
            NegativePrompt = string.IsNullOrWhiteSpace(request.NegativePrompt) ? null : request.NegativePrompt,
            Seed = request.Seed,
            Width = request.Width,
            Height = request.Height,
            BatchCount = request.BatchCount,
            OutputFormat = "png",
            SampleParams = new SampleParamsBody
            {
                SampleMethod = string.IsNullOrWhiteSpace(request.Sampler) ? null : request.Sampler,
                SampleSteps = request.Steps,
                Guidance = new GuidanceBody
                {
                    TxtCfg = request.CfgScale
                }
            }
        };
    }

    private static SdJobState MapState(JobStateResponse? payload)
    {
        var status = ParseStatus(payload?.Status);
        if (status != SdJobStatus.Completed)
        {
            return new SdJobState
            {
                Status = status,
                QueuePosition = payload?.QueuePosition
            };
        }

        var image = payload?.Result?.Images is { Count: > 0 } images ? images[0] : null;
        if (image?.B64Json is not { Length: > 0 } base64)
        {
            throw new StableDiffusionRuntimeException("The image runtime reported completion but returned no image data.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new StableDiffusionRuntimeException("The image runtime returned image data that could not be decoded.", ex);
        }

        return new SdJobState
        {
            Status = SdJobStatus.Completed,
            ImageBytes = bytes,
            Seed = image.Seed ?? payload?.Result?.Seed
        };
    }

    private static SdJobStatus ParseStatus(string? status)
    {
        return status?.ToUpperInvariant() switch
        {
            "QUEUED" => SdJobStatus.Queued,
            "GENERATING" or "RUNNING" or "IN_PROGRESS" => SdJobStatus.Generating,
            "COMPLETED" or "SUCCEEDED" or "DONE" => SdJobStatus.Completed,
            "FAILED" or "ERROR" => SdJobStatus.Failed,
            "CANCELLED" or "CANCELED" => SdJobStatus.Cancelled,
            _ => SdJobStatus.Unknown
        };
    }

    // ── sd-server /sdcpp/v1 wire DTOs (snake_case, frozen §4A) ────────────────────────────────────────────────────

    private sealed record ImgGenRequestBody
    {
        [JsonPropertyName("prompt")]
        public required string Prompt { get; init; }

        [JsonPropertyName("negative_prompt")]
        public string? NegativePrompt { get; init; }

        [JsonPropertyName("seed")]
        public long Seed { get; init; }

        [JsonPropertyName("width")]
        public int Width { get; init; }

        [JsonPropertyName("height")]
        public int Height { get; init; }

        [JsonPropertyName("batch_count")]
        public int BatchCount { get; init; }

        [JsonPropertyName("sample_params")]
        public required SampleParamsBody SampleParams { get; init; }

        [JsonPropertyName("output_format")]
        public string OutputFormat { get; init; } = "png";
    }

    private sealed record SampleParamsBody
    {
        [JsonPropertyName("sample_method")]
        public string? SampleMethod { get; init; }

        [JsonPropertyName("sample_steps")]
        public int SampleSteps { get; init; }

        [JsonPropertyName("guidance")]
        public required GuidanceBody Guidance { get; init; }
    }

    private sealed record GuidanceBody
    {
        [JsonPropertyName("txt_cfg")]
        public double TxtCfg { get; init; }
    }

    private sealed record ImgGenAcceptedResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }
    }

    private sealed record JobStateResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("queue_position")]
        public int? QueuePosition { get; init; }

        [JsonPropertyName("result")]
        public JobResultBody? Result { get; init; }

        [JsonPropertyName("error")]
        public JobErrorBody? Error { get; init; }
    }

    private sealed record JobResultBody
    {
        [JsonPropertyName("images")]
        public IReadOnlyList<JobImageBody>? Images { get; init; }

        [JsonPropertyName("seed")]
        public long? Seed { get; init; }
    }

    private sealed record JobImageBody
    {
        [JsonPropertyName("b64_json")]
        public string? B64Json { get; init; }

        [JsonPropertyName("seed")]
        public long? Seed { get; init; }
    }

    private sealed record JobErrorBody
    {
        [JsonPropertyName("code")]
        public string? Code { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }
}
