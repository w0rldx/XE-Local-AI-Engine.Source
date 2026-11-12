# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### Exit 9 naming an unknown migration after switching branches is the downgrade guard, not a broken database

**Rule:** a data directory a branch with a newer migration has run on (the AppHost data, a lab) refuses to start under an older checkout: exit 9 and a hint naming the migration. Use a fresh data directory or restore the pre-migration snapshot; never delete the history row. **Prevents:** "repairing" `__EFMigrationsHistory` by hand to get past the guard. **Authority:** `NodeDbRestoreStaging.FindUnknownAppliedMigrationAsync`, `Program.ApplyStartupMigrationsAsync`.

### A PyTorch source graph from `torch.export` on the meta device is the reference path, not the GPU path

**Rule:** treat a Model Lab PyTorch graph as the code that defines the model's maths, never as what a GPU run executes: on the meta device `torch.export` follows transformers' reference implementation, while a GPU run may switch to fused kernels (SDPA or flash attention, causal-conv1d, flash-linear-attention) the graph does not show. Say so wherever a graph or its lines are presented. **Prevents:** reading a graph node as a kernel that ran, or "fixing" a missing fused op in the index. **Authority:** `ModelLabPyTorchSourceService`, `modellab.py` source job.
