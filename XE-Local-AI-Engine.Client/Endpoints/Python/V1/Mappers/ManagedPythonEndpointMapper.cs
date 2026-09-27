namespace XE_Local_AI_Engine.Client.Endpoints.Python.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.ManagedPython;

internal static class ManagedPythonEndpointMapper
{
    public static ManagedPythonStatusResponse ToResponse(this ManagedPythonStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new ManagedPythonStatusResponse
        {
            Toolchain = new ManagedPythonToolchainResponse
            {
                UvVersion = status.Toolchain.UvVersion,
                UvPresent = status.Toolchain.UvPresent,
                PythonInstalls = status.Toolchain.PythonInstalls
            },
            Environments = status.Environments.Select(static environment => environment.ToResponse()).ToArray()
        };
    }

    private static ManagedPythonEnvironmentResponse ToResponse(this ManagedPythonEnvironmentStatus environment)
    {
        return new ManagedPythonEnvironmentResponse
        {
            ProfileId = environment.ProfileId,
            State = environment.State.ToString(),
            Reason = environment.Reason,
            Installed = environment.Installed is { } identity
                ? new ManagedPythonEnvironmentIdentityResponse
                {
                    PythonMinor = identity.PythonMinor,
                    ProfileRevision = identity.ProfileRevision,
                    Rid = identity.Rid,
                    ProbeContractVersion = identity.ProbeContractVersion,
                    UvVersion = identity.UvVersion
                }
                : null,
            Mismatches = environment.Mismatches
        };
    }
}
