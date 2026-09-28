namespace XE_Local_AI_Engine.Client.Services.Development;

public interface IDevelopmentCloudContextBuilder
{
    DevelopmentCloudContextBundle Build(DevelopmentCloudContextBuildRequest request);
}
