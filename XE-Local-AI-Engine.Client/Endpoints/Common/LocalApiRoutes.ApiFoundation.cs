namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    public static class ApiFoundation
    {
        public const string ValidationProblemProbe = "diagnostics/validation-probe";
        public const string UnhandledExceptionProbe = "diagnostics/exception-probe";
        public const string ConfiguratorCanaryProbe = "diagnostics/configurator-canary-probe";
    }
}
