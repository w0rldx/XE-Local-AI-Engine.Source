# Knowledge Base / RAG

> Last reviewed: 2026-07-24 · Code-grounded.

The Knowledge Base is a **fully offline** document store with hybrid retrieval. An operator uploads documents; the node extracts their text, chunks them on header boundaries, embeds each chunk with a local embedding model, and indexes everything into local SQLite with **selective encryption** — source document blobs and display names are encrypted at rest, while the extracted chunk text and its FTS search index are stored unencrypted locally. Retrieval fuses a **lexical** arm (SQLite FTS5 / BM25) and a **semantic** arm (vector cosine similarity) with Reciprocal Rank Fusion, optionally rescoring with a **local cross-encoder reranker**, and exposes the result to agents as a tool.

**Provider-locality invariant (enforced default).** Ingestion and embedding stay on-device by default (see `KnowledgeBase:EmbeddingProviderName`), and — as of the MED-004 hardening — the read-only knowledge tools (`search_knowledge_base`, `read_document`, `read_surrounding_chunks`) are **offered only to node-local models** (llama.cpp / Ollama). A cloud-hosted model (Codex OAuth, Azure Foundry) is **not** offered these tools, so document text, chunks, and the query are never handed to a third-party provider through a tool call. An operator can waive this by setting **`KnowledgeBase:AllowCloudModelAccess=true`** (default `false`) — an explicit, documented opt-in that acknowledges knowledge-base content may then reach the cloud provider.

The gate keys on the **EFFECTIVE model** — the one that actually runs the turn after any agent/profile pin is applied — not the turn's active model. So an agent, orchestration participant, or spawned sub-agent **pinned** to a cloud model is withheld the knowledge tools even when the conversation's active model is local (each effective model's locality is resolved through the shared `IModelCapabilityResolver`; participants and pins are classified individually, spawned children through the same resolver keyed on the child's pin). Retrieved chunk text — and its attacker-controlled metadata (title, section, source) — is additionally returned to the model fenced as untrusted **data**: a `contentTrust: "untrusted-document"` flag plus per-response nonce-delimited begin/end markers (a random marker suffix the document body cannot forge), so an instruction embedded in a document, title, or heading cannot read as a system directive or break out of the fence. Nothing reaches a log.

**Attachments and workspace file tools are gated by the same effective-model locality, with a visible notice.** Conversation attachments (inlined in plain chat, or staged into the AgentHome workspace and read via the coder `list_files`/`read_file`/`search_text` tools) are node-local private data. When the **effective** model that runs a turn is cloud-hosted — including when an agent/profile *pin* substitutes a cloud model even though the user's active pick is local — and the operator has not opted in via **`KnowledgeBase:AllowCloudModelAccess`**, the attachments are **withheld**: not staged for the file tools, not inlined into the prompt, and the coder file tools are withheld from the offer. The user gets a visible turn notice naming the effective model. (The earlier "user chose the conversation model" rationale does not hold once a pin substitutes the model, so attachments are gated exactly like the Knowledge Base rather than waved through.) The opt-in restores them. Whatever content does reach the model is fenced as untrusted data (file name inside the fence) so it is never read as instructions; the attachment fence nonce is derived from a **server secret** (HKDF over the node key), not the client-visible conversation id, so a client cannot forge the fence's closing marker.

**Config key.** `KnowledgeBase:AllowCloudModelAccess` (default `false`) is the single opt-in governing *all* node-local private-data exposure to a cloud model: the knowledge tools, the coder workspace file tools, and conversation attachments. It is named under `KnowledgeBase` for continuity; set it to `true` to allow a cloud model to receive any of that node-local content.

## Where the code lives

| Concern | Project / path |
|---|---|
| Ingestion pipeline driver | `XE-Local-AI-Engine.Client.Application/Services/Knowledge/KnowledgeIngestionService.cs` (`IKnowledgeIngestionService`) |
| Background ingestion worker + dispatcher | `…/Services/Knowledge/KnowledgeIngestionWorker.cs`, `KnowledgeIngestionDispatcher.cs` |
| Header-boundary chunker | `…/Services/Knowledge/HeaderBoundaryChunkingService.cs` (`IChunkingService`) |
| Chunk embedder | `…/Services/Knowledge/KnowledgeChunkEmbedder.cs` (`IKnowledgeChunkEmbedder`) |
| Embedding model resolver + prefixer | `…/Services/Knowledge/EmbeddingModelResolver.cs`, `KnowledgeEmbeddingPrefixer.cs` |
| Hybrid search orchestrator | `…/Services/Knowledge/KnowledgeSearchService.cs` (`IKnowledgeSearchService`) |
| Lexical arm (FTS5) | `…/Services/Knowledge/FtsSearch.cs` (`IFtsSearch`) |
| Semantic arm (cosine) + factory | `…/Services/Knowledge/ManagedCosineVectorSearch.cs`, `VectorSearchFactory.cs` (`IVectorSearch`) |
| Encrypted document blob store | `…/Services/Knowledge/KnowledgeDocumentBlobStore.cs` (`IKnowledgeDocumentBlobStore`) |
| Document text extraction | `…/Services/DocumentIngestion/DocumentTextExtractor.cs` + `Extraction/` |
| Agent-facing tools | `…/Services/Knowledge/Tools/Implementation/SearchKnowledgeBaseToolHandler.cs`, `ReadSurroundingChunksToolHandler.cs` |
| SignalR notifier + hub | `XE-Local-AI-Engine.Client/Hubs/KnowledgeIndexingNotifier.cs`, `KnowledgeBaseHub.cs` |
| Local endpoints | `XE-Local-AI-Engine.Client/Endpoints/Knowledge/V1/` |
| React feature | `XE-Local-AI-Engine.Client.React/src/features/knowledge/` |

## Ingestion pipeline

`KnowledgeIngestionService` drives one document through the pipeline, advancing `knowledge_documents.status` at each transition and setting a **content-free** `failure_reason` on any step failure. Uploads are processed asynchronously by `KnowledgeIngestionWorker` so the upload endpoint returns as soon as the document is admitted to the queue and the operator watches status over the hub.

```
Upload ──▶ Uploaded ──▶ Extracting ──▶ Chunking ──▶ Embedding ──▶ Indexed
                 │            │             │             │            │
      encrypted blob     text extract   header      local model    atomic index
      (blob store)       (.pdf/.docx/    boundaries  batches        write (FTS + vectors)
                          plaintext)     chunker
```

- **Extraction** — `DocumentTextExtractor` dispatches by extension: `.pdf` and `.docx` have dedicated readers (`.docx` via the pure-managed Open XML SDK); a broad set of plaintext types (`.txt`, `.md`, `.markdown`, `.csv`, `.tsv`, `.json`, `.jsonc`, `.log`, …) go through `PlaintextDocumentReader`. Extraction is bounded by `DocumentExtractionLimits` (a container format such as a `.docx` zip or `.pdf` can expand well beyond its on-disk size, so a decompression-bomb guard caps the extracted size).
- **Chunking** — `HeaderBoundaryChunkingService` is an **offline, deterministic** chunker with no tokenizer or external package. It walks the document's ordered element stream: each header opens a new section (maintaining an `H1 > H2` heading trail via a level stack); paragraphs and tables accumulate into their section body, which is split into character-bounded, overlapping chunks (`MaxChunkChars` / `ChunkOverlapChars`). The same document always yields the same sections and chunks.
- **Embedding** — `KnowledgeChunkEmbedder` reuses the node-local embedding resolution path: it resolves the configured embedding provider/model (`KnowledgeBaseOptions.EmbeddingProviderName` / `EmbeddingModelName`) and generates in batches (`MaxEmbeddingBatchSize`). After provider generation, ingestion and query share `KnowledgeEmbeddingVectorPolicy`. A confidently resolved `nomic-embed-text-v1.5` defaults to the versioned Matryoshka recipe (full-vector population layer norm with epsilon `1e-5` → first 512 components → L2 normalization); other models stay native. The exact resolved model + transform/version + width is persisted as the canonical vector identity and is filtered exactly during search, with the dimension checked separately as defense in depth.
- **Indexing** — the final `Indexed` transition is performed **atomically** by `IKnowledgeIndexWriter` so a document is never half-visible. All failure logging is **exception-type-only** — no chunk or document text reaches a log.
- **Admission control (backpressure)** — the async queue is bounded (`KnowledgeIngestionDispatcher`: a single-reader channel, capacity 256; the worker further caps concurrency with a `SemaphoreSlim`). Admission is **non-blocking**: when the queue is full, the upload/reindex endpoint returns **503 with `Retry-After: 5`** instead of holding the request or growing the backlog, and admission is **idempotent** (a document already queued or in flight is not re-enqueued, so it is never processed twice concurrently). A 503 is a *retryable busy* signal, not data loss — the blob is already persisted, so a document whose ingestion a full queue previously rejected (persisted-but-unindexed) is re-enqueued on a later re-upload of the same file. Accept/reject counts and live queue depth are published on the `XE.Node` meter. The synchronous **chat**-attachment path has its own equivalent gate (`DocumentExtractionAdmissionGate`), also 503 + `Retry-After` when at capacity — see the busy-admission note in [API & Hubs](09-api-and-hubs.md).

**Matryoshka rollback procedure.** Set `KnowledgeBase:EmbeddingVectorMode` to `Native`, invoke the normal corpus reindex endpoint, and wait until the catalog reports no stale documents. Only then deploy a binary that predates canonical vector identities. Switching the binary first is unsafe because old model-name-only search code cannot distinguish the 512-wide transformed rows from native vectors. Switching either direction changes the canonical identity immediately, so old projections stay excluded/stale until the normal per-document or full-corpus reindex rebuilds them.

## Hybrid retrieval

`KnowledgeSearchService.SearchAsync` is the retrieval heart. A `KnowledgeSearchRequest` carries the untrusted `Query`, a `Limit`, an optional `DocumentId` scope, and an `ExpandNeighbors` flag. The flow:

1. **Embed the query** with the current model, using the query-intent prefix (`KnowledgeEmbeddingPrefixer`).
2. **Two arms retrieve candidates in parallel:**
   - **Lexical** — `FtsSearch` runs a BM25-ranked `MATCH` over the `chunk_fts` FTS5 external-content index (the raw-SQL path; the query is escaped before it reaches FTS). A document-scoped search filters in the same `MATCH` via the UNINDEXED `document_id` column.
   - **Semantic** — the model-scoped `IVectorSearch` (`ManagedCosineVectorSearch`) scores chunk embeddings by cosine similarity within the active model/dimension.
   Each arm fetches a **candidate pool** (`CandidatePoolMultiplier` × limit, floored at `MinimumCandidatePool`) so fusion has enough overlap material.
3. **Fuse** the two ranked lists with **Reciprocal Rank Fusion** (`IRankingFusionService`).
4. **Optionally rerank** the fused candidate pool with a local **cross-encoder reranker** (`IRerankerClient`, model `KnowledgeBaseOptions.RerankerModelName`) before the top-`limit` cut; when enabled and successful the hit's `Score` becomes the cross-encoder relevance score.
5. **Hydrate** the selected chunks over the raw-SQL path and, when `ExpandNeighbors` is set, expand each hit with its surrounding neighbor chunks (`NeighborWindow = 1` each side).

**Graceful degradation is a contract:** if the embedding model or the reranker is unavailable, search degrades (lexical-only / fusion-order) rather than failing. A hit's `Title`/`Section` are derived from the **non-sensitive** `heading_path`/`storage_path`, so a result never exposes the encrypted original file name.

That invariant is a statement about the **search response**, not about what the user sees. Because `storage_path` is a GUID filename, a chunk with no heading trail would otherwise be labelled with a raw GUID — unreadable, and indistinguishable between two documents. The **UI resolves the display name client-side**: each hit carries its `document_id`, and `KnowledgeSearchPanel` joins that against the already-loaded documents list (which decrypts names for the same operator-authenticated viewer) to render `12-security-and-privacy.md › Encryption at rest`, falling back to the API's title when the document is not in the loaded list. So do not "fix" a GUID-titled result by decrypting `original_file_name` in `KnowledgeSearchService` — the readable label already exists one layer up, and moving it into the response would also hand file names to a cloud model through the gated `search_knowledge_base` tool.

## Agent tool surface

The knowledge base is exposed to the agent loop as tools:

- **`SearchKnowledgeBaseToolHandler`** — the agent issues a query and receives fused, hydrated hits (the same `IKnowledgeSearchService` path).
- **`ReadSurroundingChunksToolHandler`** — the agent expands a specific hit with its neighboring chunks for more context.

These make the KB a **retrieval-augmented generation (RAG)** source the agent can consult mid-conversation. See [Agent Mode](04-agent-mode.md) for the tool registry.

## SignalR notifications

`KnowledgeBaseHub` (`KnowledgeIndexingNotifier`) is a **server-push-only** hub: clients receive sanitized document status-change events; there are no client-callable server methods. It is protected with the same operator policy as the other local hubs because the indexing stream reveals which documents exist and are being processed. The React feature invalidates its document-list query on each event (notification-only). See [API & Hubs](09-api-and-hubs.md).

## Endpoints

Routes under `knowledge/*`, one endpoint class per file in `Endpoints/Knowledge/V1/`:

| Endpoint | Role |
|---|---|
| `UploadKnowledgeDocumentEndpoint` | Upload a document; returns immediately, ingestion runs async. |
| `ListKnowledgeDocumentsEndpoint` | List documents with their ingestion status. |
| `GetKnowledgeDocumentEndpoint` | One document's detail. |
| `DeleteKnowledgeDocumentEndpoint` | Delete a document and its chunks/index rows. |
| `SearchKnowledgeEndpoint` | Hybrid search over the corpus (or a single document). |
| `ReindexKnowledgeDocumentEndpoint` | Re-run ingestion for one document. |
| `ReindexCorpusEndpoint` | Re-run ingestion for the whole corpus (e.g. after an embedding-model change). |
| `DownloadRecommendedRerankerEndpoint` | POST: one-click download of the recommended cross-encoder reranker via the same GGUF download coordinator operator HF downloads use; idempotent no-op if already installed or in flight. |

All endpoints are loopback/local-only, operator-authenticated, and secret-redacted — see [Security & Privacy](12-security-and-privacy.md). They are surfaced to React via OpenAPI → hey-api; see [API & Hubs](09-api-and-hubs.md).

## React feature

`src/features/knowledge/` (`pages/`, `components/`, `hooks/`, `queries/`, `models/`) renders the upload surface, the document list with live ingestion status, and the search UI. It follows the standard client conventions (TanStack Query for server state, a SignalR hub that invalidates queries on push). See [React Client](10-react-client.md).

## Invariants a maintainer must respect

1. **No document/chunk/query text is ever logged** — failure logging is exception-type-only; `failure_reason` is a fixed content-free string.
2. **Vectors compare only within one model/dimension.** A dimension mismatch is a `Failed` document, not a silent wrong answer. Changing the embedding model requires a re-index (`ReindexCorpusEndpoint`).
3. **Search degrades, never 500s,** when the embedding model or reranker is unavailable.
4. **Results never expose the encrypted original file name** — `Title`/`Section` come from the non-sensitive heading/storage path only.
5. **The `Indexed` transition is atomic** — a document is never half-visible to retrieval.

## Related pages

- [Local Runtime & Providers](03-local-runtime-and-providers.md) — the embedding/reranker models resolve through the same provider seams.
- [Agent Mode](04-agent-mode.md) — the KB search/read tools in the agent tool registry.
- [Chat](05-chat.md) — file upload → chat attachments (a separate path from the KB corpus).
- [Data & Persistence](08-data-and-persistence.md) — `knowledge_documents`, chunk/FTS/vector tables, encrypted blob store, migration.
- [API & Hubs](09-api-and-hubs.md) — `/api/local/v1` mapping, the indexing hub, OpenAPI → hey-api.
- [React Client](10-react-client.md) — TanStack Query + SignalR conventions used by this feature.
- [Security & Privacy](12-security-and-privacy.md) — local-only endpoints, secret redaction, node-local privacy.
- [Architecture Overview](01-architecture-overview.md) · [Project Layout](02-project-layout.md)
