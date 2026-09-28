# Retrieval-eval live corpus

A committed, labeled corpus for measuring knowledge retrieval with real embedding and reranker models. The opt-in
live run scores it per category; the normal test suite only checks that the labels are consistent
(`RetrievalEvalLiveCorpusTests`) and that the whole corpus passes through the deterministic fixture and harness
(`RetrievalEvalLiveCorpusSmokeTests`). There are no quality floors in the normal suite: the deterministic embedder
is not a semantic model.

## Layout

- `documents/` — one file per document. The document key is its path relative to `documents/`, with `/`
  separators (`en/sourdough-starter.md`, `code/FtsSearch.cs.txt`).
  - `en/`, `de/` — English and German prose, written for this corpus. Several topics exist in both languages with
    *different* facts, so a cross-language query has a topic-near document in its own language that does not
    answer it.
  - `docs/` — product-style documentation about the pinned code, for mixed docs + code queries.
  - `long/` — handbooks. `water-utility-operations.md` (English, about 16 KB, long multi-chunk sections) and
    `genossenschaft-hausordnung.txt` (German, about 15 KB, no headings, so only character windows apply) carry the
    `long-document` answers in the middle of multi-chunk sections. The shorter handbooks carry chunk-boundary facts.
  - `distractors/` — documents that share vocabulary with an answer but do not contain it.
  - `code/` — pinned copies of repository source files (see Provenance).
- `queries.json` — the labels (schema below).
- `code-manifest.json` — origin path, commit and SHA-256 of every pinned code copy.

A private corpus with the same layout can be used instead by setting `XE_RETRIEVAL_EVAL_CORPUS_DIR` to its
directory. Never commit a private corpus.

## Query schema

| field | meaning |
|---|---|
| `id` | Unique, stable identifier used in reports. |
| `query` | The text handed to the search service. |
| `language` | Language the query is written in: `en` or `de`. |
| `category` | One of the categories below. |
| `answerable` | `false` exactly for `no-answer`. |
| `relevant` | Keys of every document that answers the query. Empty for `no-answer`. |
| `citation` | A phrase from a relevant document that a good hit contains verbatim (after lowercasing and splitting on non-alphanumerics). Scored as citation coverage and citation-anchor rate. For `chunk-boundary` it is always the second boundary phrase. |
| `anchors` | Optional. Headings of relevant documents that should appear in a hit's title or section. Plain-text and code documents have no headings, so their queries carry none. |
| `boundaryPhrases` | `chunk-boundary` only: the two halves of the fact, in document order, over exactly one relevant document. `RetrievalEvalLiveCorpus.VerifyBoundaryPhrasesSpanChunksAsync` proves they land in different chunks of the production chunker and never in one; a scorer can require both halves in the top-k instead of a document-level hit. |
| `distractors` | `lexical-distractor` only: the documents that repeat the query words without answering. |

The loader rejects unknown fields, unknown categories or languages, duplicate ids, unknown document keys, citations
or boundary phrases that no relevant document contains, and anchors that are not a heading of a relevant document.

## Categories

| category | what it measures |
|---|---|
| `en-prose` | English question, English answer, shared vocabulary. |
| `de-prose` | German question, German answer. |
| `cross-language` | German question answered only by an English document, and the reverse. `cl-05` keeps a lexical bridge on purpose (the loanword "Postmortem" occurs in the English answer), so BM25 alone can reach it; every other cross-language query is bridge-free. |
| `code-exact-symbol` | A class or member name, as a developer would paste it. |
| `code-path` | A repository file path. The retrieval-eval fixture does not ingest the path as metadata, so it is matched on the file content (class and namespace names); scores are a lower bound for production, where the path column carries FTS weight. |
| `code-conceptual` | A natural-language question about what code does. |
| `mixed-docs-code` | Needs both a documentation page and the code it describes. |
| `long-document` | A fact deep inside a long handbook, away from its start. |
| `chunk-boundary` | A fact whose two halves land in different chunks of the production chunker (`KnowledgeBaseOptions` defaults: 2000 characters, 512 tokens, 200 overlap), each at least about 180 characters away from the overlap. Checked against the real chunker; the category is only meaningful when the run ingests with production chunk sizing. |
| `multi-source` | Needs two or more documents, often across languages. |
| `no-answer` | Topic-adjacent but unanswerable from the corpus. Reported, not gated: the product has no abstention threshold. |
| `lexically-disjoint-semantic` | A paraphrase sharing no content word (four letters or more, minus function words) with its answer. Checked by a test. |
| `lexical-distractor` | The query words occur more often in a named distractor than in the answer. Checked by a test. |

## Reading the scores

- `code-exact-symbol` and `code-path` are ceiling categories: the names are unique in the corpus, so the lexical arm
  already ranks the answer first. They cannot show a reranker helping; they only show whether it hurts exact matches.
- An English-only reranker (the jina-reranker-v1 family) is judged on the English-only slices: queries written in
  English whose relevant documents are all English. Cross-language, German and mixed-language multi-source scores of such
  a model are reported but are not evidence against it. For the same reason its sanity gate skips the German pair:
  declare the model's languages with a suffix on its `XE_RETRIEVAL_EVAL_RERANKERS` entry (`jina=/path.gguf@en`, or
  `@en,de`; no suffix means every language). The runner's `--reranker-preset all` declares jina as `@en`.
- `no-answer` is reported, not gated: the product has no abstention threshold.

## How labels are judged

A document is relevant when a reader could answer the query from it alone, or, for `multi-source` and
`mixed-docs-code`, when it supplies a necessary part of the answer. Mentioning the topic is not enough. A
`no-answer` query must not be answerable from any document, including the distractors.

Labels frozen 2026-09-28 before the first scored run; any later change needs a new corpus revision noted in the report.

Labels are reviewed before the first scored run and are frozen afterwards: never tune a query, label or document
after seeing results. A label that turns out to be wrong is corrected in its own reviewed change, and scores from
before and after that change are not compared.

## Provenance

Prose, documentation and handbooks were written for this corpus; they contain no copyrighted text and no personal
data. The `code/` documents are verbatim copies of `XE-Local-AI-Engine.Client.Application` source files at commit
`862b2a9c22d63675a41c74192ec66147ee4a8a90`, listed with their origin path in `code-manifest.json`. They are copies,
not live paths, so the corpus never drifts when the product changes; a test checks every copy against its recorded
hash. The copies carry a `.cs.txt` suffix so the SDK does not compile them and the repository's source scanners do
not treat them as product code; the plain-text reader extracts `.cs` and `.txt` identically.
