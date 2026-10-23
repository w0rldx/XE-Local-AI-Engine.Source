namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    public static class Benchmarks
    {
        public const string Projects = "benchmarks/projects";
        public const string ProjectById = "benchmarks/projects/{projectId}";
        public const string ProjectRuns = "benchmarks/projects/{projectId}/runs";

        // The whole model x KV-type matrix in one call. Its own route rather than a flag on ProjectRuns: it answers
        // 200 with a per-item outcome list, not the 202 + one run detail a single start answers with.
        public const string ProjectRunsBatch = "benchmarks/projects/{projectId}/runs/batch";

        // One project's whole record as a downloadable file: the JSON export carries every run at full detail
        // (transcript and judge verdict included), the CSV the same runs as flat, spreadsheet-ready rows.
        public const string ProjectExport = "benchmarks/projects/{projectId}/export";
        public const string ProjectExportCsv = "benchmarks/projects/{projectId}/export.csv";
        public const string RunById = "benchmarks/runs/{runId}";
        public const string RunCancel = "benchmarks/runs/{runId}/cancel";
        public const string RunScore = "benchmarks/runs/{runId}/score";
        public const string RunRejudge = "benchmarks/runs/{runId}/rejudge";

        // The reviewer's decision is its own sub-resource: in a review project it is the only writer of the run's user score, which the score route refuses there.
        public const string RunReview = "benchmarks/runs/{runId}/review";

        // The review inbox spans projects, so it hangs off the module root rather than one project.
        public const string Reviews = "benchmarks/reviews";
        public const string RunArtifactById = "benchmarks/runs/{runId}/artifacts/{artifactId}";

        // A project's task items are their own sub-resource, one level down from the project: a collection with its own lifecycle (add, edit, delete, reorder), and each
        // write recomputes the project's item-set hash, which is not something a field on the project PUT could express.
        public const string ProjectTaskItems = "benchmarks/projects/{projectId}/items";
        public const string ProjectTaskItemById = "benchmarks/projects/{projectId}/items/{itemId}";

        // The measurement CELLS of a project: one model, one KV type, one repeat of the whole item suite. Its own route rather than a shape on ProjectRuns, because a
        // cell is what ranks and a run list cannot say which items a cell is MISSING — the absence is the answer.
        public const string ProjectCells = "benchmarks/projects/{projectId}/cells";

        // Two to six named cells and the paired difference between each pair of them. Its own route rather than a flag on ProjectCells: a comparison is over a SELECTION,
        // and the interval is computed from the items that selection shares -- a number that does not exist until someone says which cells they mean.
        public const string ProjectCompare = "benchmarks/projects/{projectId}/compare";

        // Reordering is its own verb on its own route: it names the whole order at once, which is also what makes it
        // safe under a concurrent add or delete.
        public const string ProjectTaskItemOrder = "benchmarks/projects/{projectId}/items/order";

        // The judge policy is its own sub-resource: it is the one part of a FROZEN project an operator may still
        // change, and doing so re-judges every run, so it never rides along on the project PUT.
        public const string ProjectJudge = "benchmarks/projects/{projectId}/judge";
        public const string ProjectRejudge = "benchmarks/projects/{projectId}/rejudge";

        // Quant fidelity is a display-only axis measured by its own work kind, so it gets its own sub-resources rather than flags on the run or project routes. Like the judge policy it is
        // a part of a FROZEN project an operator may still change: it sets what gets measured NEXT, not what existing runs were measured against, so it never rides the project PUT the freeze refuses.
        public const string ProjectFidelity = "benchmarks/projects/{projectId}/fidelity";

        public const string ProjectKldEstimate = "benchmarks/projects/{projectId}/fidelity/kld-estimate";
        public const string ProjectFidelityCache = "benchmarks/projects/{projectId}/fidelity/cache";
        public const string RunFidelity = "benchmarks/runs/{runId}/fidelity";

        // The verdict matrix and the fit it produced are ONE question: a score shown beside a verdict set that did
        // not produce it is undetectable from the client, so they are never split across two routes.
        public const string ProjectComparisons = "benchmarks/projects/{projectId}/comparisons";
        public const string ProjectPairwiseEstimate = "benchmarks/projects/{projectId}/pairwise-estimate";
        public const string RubricPresets = "benchmarks/rubric-presets";
        public const string EligibleAgents = "benchmarks/eligible-agents";
        public const string EligibleModels = "benchmarks/eligible-models";
        public const string Hub = "/api/local/v1/benchmarks/hub";
    }
}
