namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Node-wide agent skill library routes. Skills are SKILL.md documents (name + description + markdown body)
    ///     that agent definitions select into via <c>AllowedSkillIds</c> and load on demand at runtime.
    /// </summary>
    public static class Skills
    {
        // Skill collection (GET list — body omitted; POST create) and the individual skill resource (GET full incl.
        // body, PUT, DELETE).
        public const string Definitions = "skills";
        public const string DefinitionById = "skills/{skillId}";

        // Two-phase third-party import. The literal "import" segment sits under the collection, not the {skillId}
        // param, so it can never be parsed as an id. Preview writes nothing; the commit replays the previewed payload.
        public const string ImportPreview = "skills/import/preview";
        public const string Import = "skills/import";

        // One bundled file's content read out of a live preview, so it can be reviewed before the commit. Same
        // percent-escaped {resourceName} rule as ResourceByName; {skillName} is matched NOCASE like the commit's selection.
        public const string ImportPreviewResource = "skills/import/preview/{token}/skills/{skillName}/resources/{resourceName}";

        // AI-assisted drafting, same literal-segment-under-the-collection rule as "import". Writes nothing — the draft
        // only populates the operator's form; the existing create/update routes stay the sole persistence path.
        public const string Draft = "skills/draft";

        // Bundled skill files. {resourceName} is a skill-root-relative path, so it can carry slashes: the client
        // percent-escapes it and the endpoint decodes + validates before the lookup (see SkillResourceRouteName).
        public const string Resources = "skills/{skillId}/resources";
        public const string ResourceByName = "skills/{skillId}/resources/{resourceName}";
    }
}
