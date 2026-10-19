namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>How a <c>/v1/systemone</c> call ended; everything but <see cref="Answered" /> carries no answer.</summary>
public enum LlamaServerSystemOneOutcome
{
    Answered = 0,

    /// <summary>HTTP 501: the loaded model is not a decision model.</summary>
    NotDecisionModel = 1,

    /// <summary>The server refused the request: HTTP 400, or the HTTP 500 a prompt larger than one physical batch gets.</summary>
    InvalidRequest = 2,

    /// <summary>The call ran out of its own budget before the server answered.</summary>
    Timeout = 3,

    /// <summary>Any other non-2xx status or a transport error.</summary>
    Transport = 4,

    /// <summary>A 2xx whose body carried no usable answer for the question.</summary>
    Malformed = 5,

    /// <summary>The decision process could not be started.</summary>
    Unavailable = 6,

    /// <summary>An operator eject is draining the process.</summary>
    Evicting = 7,

    /// <summary>A profiling or benchmark spawn owns the process right now.</summary>
    Busy = 8
}
