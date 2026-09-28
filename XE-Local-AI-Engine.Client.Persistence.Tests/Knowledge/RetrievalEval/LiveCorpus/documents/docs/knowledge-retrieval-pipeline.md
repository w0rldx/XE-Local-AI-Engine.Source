# Knowledge retrieval pipeline

This page explains how a knowledge-base search turns a question into ranked excerpts. It is written for operators
who want to understand why a document did or did not show up.

## Two retrieval arms

Every search runs two independent arms over the indexed chunks. The lexical arm is a full-text index that matches
the words of the question; the semantic arm compares an embedding of the question against the stored chunk
embeddings. Each arm produces its own ranked candidate list.

The lexical arm deliberately joins the words of the question with OR, so a chunk that contains any single word can
become a candidate. Chunks that contain more of the words still rank higher. Headings and code symbols count more
than body text, which is why a question that names a class or a section title usually finds it.

## Fusing the arms

The two lists are merged by rank, not by raw score, because the lexical and semantic scores live on different scales.
A chunk earns a contribution from every list it appears in, and a chunk that both arms like beats a chunk that only
one arm likes. An optional score-aware tilt lets a large score gap inside one arm nudge the order, without ever
letting that arm dominate.

## Reranking

When a reranker model is configured, the fused candidates are rescored by a cross-encoder that reads the question
and each candidate together. Reranking has a time budget per search; if the budget runs out, the fused order is kept
and the search still returns results.

## Why a document is missing

The most common reasons are that it was never indexed, that its embedding model differs from the current one and a
re-index is pending, or that the question uses words the document never uses and the semantic arm ranks it below
the cut-off.
