# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### Embedding profile: chosen by name, part of the vector identity
**Rule:** an embedding model's prompt format and pooling come from `EmbeddingModelProfile.For`, matched on the model segment of the resolved name (after the last `/`, cut at the first `:`), never from GGUF architecture or pooling metadata. The profile's identity tag is part of the stored vector identity, and any change to a profile's prefix or pooling bumps its tag version. **Prevents:** chat Qwen3 and Qwen3-Embedding sharing one profile (one architecture id); the launcher and the knowledge base disagreeing about one shared `(model, Embedding)` process; a format or pooling change mixing old and new vectors under one identity instead of staling only that profile's rows. **Authority:** `EmbeddingModelProfile`, `KnowledgeEmbeddingVectorPolicy`, `EmbeddingModelProfileTests.EveryTag_NamesItsPooling_SoAPoolingChangeCannotKeepItsTag`.
