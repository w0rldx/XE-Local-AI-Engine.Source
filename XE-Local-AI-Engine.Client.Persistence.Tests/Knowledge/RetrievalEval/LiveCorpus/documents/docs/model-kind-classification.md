# How installed models are classified

The model list sorts every model into chat, embedding, reranker, or unknown. The kind decides where a model may
appear: only chat models are offered in the chat picker, and knowledge-base indexing only picks embedding models.

## Order of the checks

A reranker is recognised by its name first, before anything else. Rerankers score a pair of texts instead of
generating text, and the model server advertises no special marker for them. A name such as a BGE reranker would
otherwise be taken for an embedding model, because BGE names are also used by embedding models.

After that, the capabilities reported by the model server decide: a model that can embed but not complete text is an
embedding model, and any model that can complete text is a chat model.

## When no capabilities are reported

Older servers and offline scans report nothing. Then only the name is used, conservatively: well-known embedding
name patterns mark an embedding model, but a model is never guessed to be a chat model from its name alone.

## Thinking and tools

A model only receives the "think" switch or tool offers when the server reports those capabilities. Missing
capabilities count as "not supported", which avoids request errors from models that would reject them.
