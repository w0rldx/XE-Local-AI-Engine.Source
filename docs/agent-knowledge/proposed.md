# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### A pinned thinking budget overwrote the thinking-off zero budget on the llama wire

**Rule:** Write the thinking-budget marker only when thinking is on (`think` is not `false`); a user or benchmark pin never applies under reasoning off. **Prevents:** `ApplyReasoningBudget` patching the pin over the `reasoning_budget_tokens: 0` that `ApplyThinkingSwitch` sends, which re-enables thinking on a template that honours only the zero budget (LFM2.5) and lets a pinned run think while the user chose Off. **Authority:** `InvocationAgentFactory` (the `think is not false` gate) and its reasoning-budget tests; Direct Inference applies the same rule in `DirectInferenceService`.
