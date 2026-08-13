namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using System.Text;

/// <summary>
///     Deterministic scenario families beyond the compatibility baseline. These cases exercise multilingual lexical
///     retrieval, exact code anchors, distractors, chunk boundaries, multiple relevant sources, and explicit abstention.
/// </summary>
internal static class RetrievalEvalRepresentativeCorpus
{
    public static IReadOnlyList<RetrievalEvalCorpus.FixtureDocument> Documents { get; } =
    [
        new("english-canary",
            """
            # Canary deployment

            A canary rollout pauses when the error budget exceeds seven percent. Operators then revert the green
            deployment slot and keep the stable blue slot serving traffic until the incident review is complete.
            """),
        new("english-distractor",
            """
            # Canary bird habitat

            The Atlantic canary builds a nest in shrubs and eats grass seed. Bird keepers provide daylight, fresh
            water, and a wide flight enclosure for healthy feathers.
            """),
        new("german-backup",
            """
            # Datenbanksicherung

            Die verschlüsselte Datenbanksicherung wird jeden Dienstag um 03:15 Uhr erstellt. Der
            Wiederherstellungstest prüft anschließend das Archiv und protokolliert die Prüfsumme.
            """),
        new("german-distractor",
            """
            # Datenbankabfrage

            Eine Datenbankabfrage filtert aktive Datensätze und sortiert die Ergebnisliste nach dem Zeitstempel.
            Diese Anleitung beschreibt keine Sicherung und keinen Wiederherstellungstest.
            """),
        new("code-writer",
            """
            # Knowledge index writer

            The exact method identifier is KnowledgeIndexWriter.WriteAsync. Its implementation lives at
            XE-Local-AI-Engine.Client.Persistence/Knowledge/KnowledgeIndexWriter.cs and commits chunks atomically.
            """),
        new("code-distractor",
            """
            # Knowledge search reader

            KnowledgeSearchService.SearchAsync reads indexed chunks. The implementation is in
            XE-Local-AI-Engine.Client.Application/Services/Knowledge/KnowledgeSearchService.cs.
            """),
        new("long-boundary", BuildLongBoundaryDocument()),
        new("retention-policy",
            """
            # Retention policy

            The compliance retention schedule keeps audit exports for thirteen months. Policy owners approve any
            extension and record the approval reference in the archive register.
            """),
        new("retention-runbook",
            """
            # Retention runbook

            The archive runbook applies the thirteen month retention schedule, verifies the audit export checksum,
            and records the storage location before old snapshots are removed.
            """),
        new("irrelevant-gardening",
            """
            # Balcony gardening

            Basil seedlings need drainage, morning sunlight, and regular watering in a sheltered balcony planter.
            """)
    ];

    public static IReadOnlyList<LabeledQuery> AnswerableQueries { get; } =
    [
        new("representative-english", "seven percent error budget revert green deployment slot", "english-canary",
            "revert the green deployment slot")
        {
            ScenarioGroup = "english",
            SourceAnchors = ["Canary deployment"]
        },
        new("representative-german", "Wann wird die verschlüsselte Datenbanksicherung erstellt", "german-backup",
            "jeden Dienstag um 03 15 Uhr")
        {
            ScenarioGroup = "german",
            SourceAnchors = ["Datenbanksicherung"]
        },
        new("representative-code",
            "KnowledgeIndexWriter.WriteAsync XE-Local-AI-Engine.Client.Persistence/Knowledge/KnowledgeIndexWriter.cs",
            "code-writer",
            "KnowledgeIndexWriter WriteAsync")
        {
            ScenarioGroup = "code-exact-identifier-path",
            SourceAnchors = ["Knowledge index writer"]
        },
        new("representative-distractor", "canary rollout error budget stable blue traffic", "english-canary",
            "stable blue slot serving traffic")
        {
            ScenarioGroup = "distractor",
            SourceAnchors = ["Canary deployment"]
        },
        new("representative-boundary", "boundary sentinel cobalt narwhal recovery", "long-boundary",
            "boundary sentinel cobalt narwhal")
        {
            ScenarioGroup = "long-document-boundary",
            SourceAnchors = ["Long boundary handbook"]
        },
        new("representative-multi-source", "thirteen month retention schedule audit export archive", "retention-policy",
            "thirteen month retention schedule")
        {
            RelevantDocumentKeys = ["retention-policy", "retention-runbook"],
            ScenarioGroup = "multi-source",
            SourceAnchors = ["Retention policy", "Retention runbook"]
        }
    ];

    public static IReadOnlyList<LabeledQuery> NoAnswerQueries { get; } =
    [
        new("representative-no-answer", "quantum pineapple orbital spectrometer", string.Empty, string.Empty)
        {
            RelevantDocumentKeys = [],
            ExpectsNoAnswer = true,
            ScenarioGroup = "no-answer"
        }
    ];

    private static string BuildLongBoundaryDocument()
    {
        var body = new StringBuilder("# Long boundary handbook\n\n");
        for (var index = 0; index < 34; index++)
        {
            _ = body.Append("routine filler passage ");
        }

        _ = body.Append("boundary sentinel cobalt narwhal recovery procedure ");
        for (var index = 0; index < 34; index++)
        {
            _ = body.Append("continuation filler passage ");
        }

        return body.ToString();
    }
}
