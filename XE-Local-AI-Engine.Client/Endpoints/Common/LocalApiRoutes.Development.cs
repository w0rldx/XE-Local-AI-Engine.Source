namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>Development project, task, evidence, apply, and live-attempt routes.</summary>
    public static class Development
    {
        public const string Root = "development";
        public const string Capability = "development/capability";

        // Operator approval of the container runtime the capability preflight reports. A POST because
        // it changes what this node has approved; separate from the capability GET so a read can never pin a daemon.
        public const string ContainerRuntimeConfirmation = "development/container-runtime/confirmation";
        public const string Repositories = "development/repositories";

        // A GET with the folder id in the route: detection is a read of the repository, it takes no body, and a
        // body-less POST would land on this repo's 415 trap.
        public const string RepositoryProfileDetection = "development/repositories/{selectedFolderId}/profile-detection";

        // Templates are ordinary repositories the operator already has; the registry is list/add/remove, and materializing one produces a NEW registered repository,
        // which is why the create route sits under repositories rather than under templates.
        public const string Templates = "development/templates";
        public const string TemplateById = "development/templates/{templateId}";
        public const string RepositoriesFromTemplate = "development/repositories/from-template";
        public const string Projects = "development/projects";
        public const string ProjectById = "development/projects/{projectId}";
        public const string RepositoryConnection = "development/projects/{projectId}/repository-connection";
        public const string TaskById = "development/projects/{projectId}/tasks/{taskId}";
        public const string NextAction = "development/projects/{projectId}/tasks/{taskId}/next-action";
        public const string CancelAttempt = "development/projects/{projectId}/tasks/{taskId}/attempts/{attemptId}/cancel";
        public const string Events = "development/projects/{projectId}/events";
        public const string TaskArtifacts = "development/projects/{projectId}/tasks/{taskId}/artifacts";
        public const string ArtifactById = "development/projects/{projectId}/tasks/{taskId}/artifacts/{artifactId}";
        public const string PatchPreview = "development/projects/{projectId}/tasks/{taskId}/preview";
        public const string Apply = "development/projects/{projectId}/tasks/{taskId}/apply";
        public const string Hub = "/api/local/v1/development/hub";
    }
}
