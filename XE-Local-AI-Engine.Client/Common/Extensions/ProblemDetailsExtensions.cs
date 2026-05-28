namespace XE_Local_AI_Engine.Client.Common.Extensions;

using Microsoft.AspNetCore.Mvc;

public static class ProblemDetailsExtensions
{
    public static TProblemDetails WithTraceId<TProblemDetails>(this TProblemDetails problemDetails, HttpContext httpContext)
        where TProblemDetails : ProblemDetails
    {
        ArgumentNullException.ThrowIfNull(problemDetails);
        ArgumentNullException.ThrowIfNull(httpContext);

        problemDetails.Extensions["traceId"] = httpContext.TraceIdentifier;

        return problemDetails;
    }
}
