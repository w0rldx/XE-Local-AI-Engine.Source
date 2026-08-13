# Deterministic RAG capacity benchmark

`RetrievalCapacityBenchmarkTests` is an offline, fixed-seed capacity lane over the real SQLite FTS5 index,
managed exact-vector scanner, score-aware fusion, and hydration query. It uses two or more collection namespaces and
contains English, German, exact code identifier/path, strong-distractor, and explicit no-answer cases.
Vectors use the production 512-dimensional Matryoshka width. Chunks are distributed as evenly as possible across the
declared namespaces, and the report includes `largestNamespaceChunkCount` so collection-scoped vector-scan cost is
interpreted against the searched namespace rather than only the aggregate corpus size.

The ordinary test suite runs only the 256-chunk smoke profile. The larger profiles are opt-in:

```bash
scripts/with-build-lock.sh -- \
  dotnet build XE-Local-AI-Engine.Client.Persistence.Tests/XE-Local-AI-Engine.Client.Persistence.Tests.csproj \
  --configuration Release --no-restore

XE_RAG_CAPACITY_PROFILE=10k \
scripts/with-build-lock.sh -- scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test XE-Local-AI-Engine.Client.Persistence.Tests/XE-Local-AI-Engine.Client.Persistence.Tests.csproj \
    --configuration Release --no-build \
    --treenode-filter '/*/*/RetrievalCapacityBenchmarkTests/OptInProfile_*'
```

Valid profiles are `10k`, `100k`, `250k`, `500k`, and `1m`. A run prints and can persist:

- schema, corpus-row build, FTS rebuild, vector-index build, and total build time;
- FTS, managed vector scan, fusion, and end-to-end p50/p95/max latency;
- recall@5, MRR, nDCG@5, and the no-answer false-positive rate of the complete lexical+dense pipeline;
- SQLite database footprint plus sampled process working-set and managed-heap high-water marks.

The current production retrieval path has no confidence threshold, so an unmatched dense query still returns nearest
neighbors. The benchmark records that honestly as a no-answer false-positive (currently expected to be `1.0`) rather
than skipping dense search and claiming vacuous rejection accuracy. Query latency is measured after one warm-up per
query shape; memory is process-only and sampled, excluding filesystem page cache and other system-wide consumers. The
JSON report labels this mode as `warm-cache-process-only`.

Persist a machine-readable JSON report with `XE_RAG_CAPACITY_REPORT=/absolute/path/report.json`.

The 500 ms end-to-end p95 target is always reported. Stopwatch measurements in the default smoke are deliberately not
a performance assertion. Gate a specifically declared capacity profile only when the host is controlled and suitable
for a capacity decision:

```bash
XE_RAG_CAPACITY_PROFILE=100k \
XE_RAG_CAPACITY_ENFORCE_P95=1 \
XE_RAG_CAPACITY_P95_TARGET_MS=500 \
XE_RAG_CAPACITY_REPORT=/tmp/rag-capacity-100k.json \
scripts/with-build-lock.sh -- scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test XE-Local-AI-Engine.Client.Persistence.Tests/XE-Local-AI-Engine.Client.Persistence.Tests.csproj \
    --configuration Release --no-build \
    --treenode-filter '/*/*/RetrievalCapacityBenchmarkTests/OptInProfile_*'
```

Exit 75 means the test assemblies changed during the run; the result is void and must be rerun.

The benchmark fails rather than reporting a vacuous green when no queries, no answerable queries, or no answerable
results were observed. The corpus measures deterministic retrieval mechanics and capacity; it does not claim real-model
semantic quality.
