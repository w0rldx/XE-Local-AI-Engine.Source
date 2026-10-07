# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### MXC ProcessContainer: host prep, one command line, a verbatim environment

**Rule:** treat an MXC `Probe` warning naming `wxc-host-prep` as "unavailable", never as a warning: the one-time `prepare-system-drive` and the per-boot `prepare-null-device` are admin steps the product never runs, and without them children fail to start. `ContainerRequest.Command` is one command LINE, so quote argv with `WindowsCommandLine.Join` (CommandLineToArgvW rules), never `string.Join(" ")`. A non-null `Environment` is used verbatim: pass `SystemRoot`, `windir`, `ComSpec`, `PATHEXT`, `PATH` and a jail `TEMP`, or the launch fails. **Prevents:** a host advertising a boundary whose every launch dies, argv split differently inside the container, and a child that cannot find `cmd.exe`. **Authority:** ADR 0019; `HostSandboxContainmentProbe.MeasureWindows`, `WindowsCommandLine`, `SandboxLauncher.BuildAppContainerEnvironment`.

### Cloud-model tool gates live only in the offer (target: agents-and-sandbox.md)

**Rule:** a tool's cloud-model gate is the `LocalToolOfferProvider` offer branch (plus the `SubAgentSpawnService` seam guard for spawn); no executor (`WebFetchService`, `WebSearchService`, `CustomToolCatalog`, `McpToolRegistry`, `InvocationToolResolver`) re-checks locality. A new tool class that must stay off cloud models needs its own offer branch keyed on `IModelTrustResolver` (Unresolved counts as cloud) and, if it is opened by a Privacy switch, the sync getter read on the same path. Unattended callers must pass the real cloud flag into the offer, never a hard-coded `false`. **Prevents:** a tool reaching cloud models ungated, as MCP tools did before `AllowCloudModelMcpTools`, and a cloud unattended run getting the local-data offer. **Authority:** `LocalToolOfferProviderTests`, `SubAgentSpawnServiceTests`, `GraphWorkflowAgentExecutorTests`.
