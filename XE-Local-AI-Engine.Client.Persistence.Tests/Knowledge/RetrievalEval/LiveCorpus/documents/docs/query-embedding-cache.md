# Search question cache

Turning a question into an embedding costs a round trip to the embedding model. Users often repeat a question or
page through results, so recent question embeddings are kept in memory for a short time.

## What is stored

The cache never keeps the question text itself. Each entry is filed under a one-way hash of the question together
with an identifier of the embedding model and its settings, so switching models never serves a stale vector.

## Limits

The cache has a time-to-live and a maximum number of entries, both configurable. A time-to-live of zero turns the
cache off entirely. On top of that, a fixed memory ceiling of two megabytes applies, because large embedding
models produce much larger vectors and the entry count alone would not bound memory.

## Observability

Hits, misses and the bytes evicted are exported as metrics, which makes it easy to see whether the cache is too small
for the workload.
