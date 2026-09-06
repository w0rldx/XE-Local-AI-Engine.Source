// The one Graph Workflows surface, in two modes over one selection. Router-free by design (it takes `selection` and
// `onSelectionChange` as props, the way `DevWorkflowDetailPage` does), so it renders directly in a unit test and
// `routes/_layout/graph-workflows.tsx` stays a thin adapter.
//
// `runId` set ⇒ the run view; unset ⇒ the editor. Everything below is composition: every component, hook, query and
// model already exists, and this file owns only what none of them can own alone — which query feeds which component,
// what a Save actually does, and where the config panel lives at this viewport width.
//
// Two pieces of state are page-local rather than search params, on purpose. `selectedEdgeId` is not linkable (an edge
// key is meaningless without the graph that was open, and the plan puts only `nodeKey` in the URL), and the server's
// validation errors are pinned to the graph they were computed FOR, so a later edit drops them without an effect.

import { Alert, Button, Drawer, Loader, Paper, Stack, Tabs, Text } from "@mantine/core";
import { IconAlertTriangle, IconSitemap } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { TWO_PANE_BREAKPOINT } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { FullHeightPage } from "@/core/ui/components/FullHeightPage/FullHeightPage";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { useUnsavedChangesGuard } from "@/core/ui/hooks/useUnsavedChangesGuard";
import { toast } from "@/core/ui/notifications/Toast";
import { graphWorkflowConflictTypes, readGraphWorkflowConflict } from "@/features/graphWorkflows/api/GraphWorkflowConflict";
import { GraphWorkflowDefinitionList } from "@/features/graphWorkflows/components/GraphWorkflowDefinitionList";
import { GraphWorkflowDefinitionMetaDialog } from "@/features/graphWorkflows/components/GraphWorkflowDefinitionMetaDialog";
import { GraphWorkflowEdgeConfigPanel } from "@/features/graphWorkflows/components/GraphWorkflowEdgeConfigPanel";
import { GraphWorkflowEditorCanvas } from "@/features/graphWorkflows/components/GraphWorkflowEditorCanvas";
import { GraphWorkflowEventsTab } from "@/features/graphWorkflows/components/GraphWorkflowEventsTab";
import { GraphWorkflowNodeConfigPanel } from "@/features/graphWorkflows/components/GraphWorkflowNodeConfigPanel";
import { GraphWorkflowNodePanel } from "@/features/graphWorkflows/components/GraphWorkflowNodePanel";
import { GraphWorkflowNodeRunTable } from "@/features/graphWorkflows/components/GraphWorkflowNodeRunTable";
import { GraphWorkflowRunGraphView } from "@/features/graphWorkflows/components/GraphWorkflowRunGraphView";
import { GraphWorkflowRunList } from "@/features/graphWorkflows/components/GraphWorkflowRunList";
import { GraphWorkflowRunToolbar } from "@/features/graphWorkflows/components/GraphWorkflowRunToolbar";
import { GraphWorkflowStartRunDialog } from "@/features/graphWorkflows/components/GraphWorkflowStartRunDialog";
import { GraphWorkflowValidationStrip } from "@/features/graphWorkflows/components/GraphWorkflowValidationStrip";
import { useGraphWorkflowEditor } from "@/features/graphWorkflows/hooks/useGraphWorkflowEditor";
import { useGraphWorkflowRunHub } from "@/features/graphWorkflows/hooks/useGraphWorkflowRunHub";
import { graphToCanvas } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import type { GraphWorkflowGraph, GraphWorkflowSelection } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { type GraphWorkflowGraphIssue, serverErrorsToIssues } from "@/features/graphWorkflows/models/GraphWorkflowValidation";
import { toGraphWorkflowRunCanvas } from "@/features/graphWorkflows/models/GraphWorkflowRunGraph";
import {
	useCreateGraphWorkflowDefinition,
	useDeleteGraphWorkflowDefinition,
	useGraphWorkflowAgentOptions,
	useGraphWorkflowDefinition,
	useGraphWorkflowDefinitions,
	useGraphWorkflowModelOptions,
	useGraphWorkflowRun,
	useGraphWorkflowRuns,
	useGraphWorkflowTools,
	useUpdateGraphWorkflowDefinition,
	useValidateGraphWorkflowDefinition,
} from "@/features/graphWorkflows/queries/useGraphWorkflows";

/** What a brand-new workflow starts as: the smallest graph the server accepts, already laid out. */
const STARTER_GRAPH: GraphWorkflowGraph = {
	schemaVersion: 1,
	nodes: [
		{ key: "start", kind: "Start", label: "Start", position: { x: 0, y: 0 }, config: { inputSchema: null, defaultInput: null } },
		{ key: "end", kind: "End", label: "End", position: { x: 0, y: 160 }, config: { outcome: "completed", resultPath: null } },
	],
	edges: [{ key: "e1", from: "start", to: "end" }],
};

/** Cleared canvas when nothing is open. Module scope so the reset effect's identity never changes. */
const EMPTY_GRAPH: GraphWorkflowGraph = { schemaVersion: 1, nodes: [], edges: [] };

/** Referentially stable, so `issues` only changes when an issue actually does. */
const NO_ISSUES: readonly GraphWorkflowGraphIssue[] = [];

export interface GraphWorkflowsPageProps {
	/** Which definition, run, node and tab the URL is on. Read here, never derived from page state. */
	selection: GraphWorkflowSelection;
	onSelectionChange: (next: GraphWorkflowSelection) => void;
}

export function GraphWorkflowsPage({ selection, onSelectionChange }: GraphWorkflowsPageProps) {
	const { t } = useTranslation();
	// `useWindowDimensions` (unlike `useMediaQuery`) reads `innerWidth` synchronously on the first render, so the
	// two-pane layout never flashes as a drawer before settling — Preview's canvas made the same call.
	const { width } = useWindowDimensions();
	const isNarrow = width < TWO_PANE_BREAKPOINT;

	return (
		<FullHeightPage data-testid="graph-workflows-page">
			<Stack gap="sm" h="100%" style={{ minHeight: 0 }}>
				<PageHeader
					title={t("pages.graphWorkflows.title", "Graph Workflows")}
					icon={<IconSitemap size={24} />}
					subtitle={t("pages.graphWorkflows.subtitle", "Author a workflow graph and watch a run of it node by node.")}
				/>
				{selection.runId === undefined ? (
					<GraphWorkflowEditorMode selection={selection} onSelectionChange={onSelectionChange} isNarrow={isNarrow} />
				) : (
					<GraphWorkflowRunMode selection={selection} onSelectionChange={onSelectionChange} isNarrow={isNarrow} />
				)}
			</Stack>
		</FullHeightPage>
	);
}

interface ModeProps {
	readonly selection: GraphWorkflowSelection;
	readonly onSelectionChange: (next: GraphWorkflowSelection) => void;
	readonly isNarrow: boolean;
}

// ---------------------------------------------------------------------------------------------------------------
// Editor mode
// ---------------------------------------------------------------------------------------------------------------

type MetaDialogMode = "create" | "rename" | "saveAs";

function GraphWorkflowEditorMode({ selection, onSelectionChange, isNarrow }: ModeProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const editor = useGraphWorkflowEditor(undefined);
	// Selecting a node or a tab writes a search param, which is a router transition like any other. Without this the
	// guard asked the operator to discard their work in order to configure the node they had just added.
	useUnsavedChangesGuard({ isDirty: editor.isDirty, allowSameRoute: true });

	const definitionId = selection.definitionId;
	const definitionsQuery = useGraphWorkflowDefinitions();
	const definitionQuery = useGraphWorkflowDefinition(definitionId);
	const definition = definitionQuery.data;

	// The three pickers only exist inside the node config panel, so they are only asked for once a node is open.
	const hasNodeSelection = selection.nodeKey !== undefined;
	const toolsQuery = useGraphWorkflowTools({ enabled: hasNodeSelection });
	const agentOptionsQuery = useGraphWorkflowAgentOptions({ enabled: hasNodeSelection });
	const modelOptionsQuery = useGraphWorkflowModelOptions({ enabled: hasNodeSelection });

	const createMutation = useCreateGraphWorkflowDefinition();
	const updateMutation = useUpdateGraphWorkflowDefinition();
	const deleteMutation = useDeleteGraphWorkflowDefinition();
	const validateMutation = useValidateGraphWorkflowDefinition();

	const [selectedEdgeId, setSelectedEdgeId] = useState<string | undefined>(undefined);
	const [metaDialog, setMetaDialog] = useState<MetaDialogMode | undefined>(undefined);
	const [startOpened, setStartOpened] = useState(false);
	const [saveConflict, setSaveConflict] = useState(false);
	const [deleteError, setDeleteError] = useState<string | undefined>(undefined);
	// The server's answer, PINNED to the graph it answered about. Any edit mints a new `editor.graph` object, so the
	// stale errors drop out on the next render with no effect and no manual clearing.
	const [validated, setValidated] = useState<
		{ readonly graph: GraphWorkflowGraph; readonly issues: readonly GraphWorkflowGraphIssue[] } | undefined
	>(undefined);

	// Load a definition into the canvas exactly once per `id:version`. Keyed on the version rather than on the query
	// data, because a refetch that answers the SAME version must never throw away edits in progress.
	const loadedKey =
		definition?.id !== undefined && definition.version !== undefined ? `${definition.id}:${definition.version}` : undefined;
	const loadedGraph = definition?.graph;
	const loadedRef = useRef<string | undefined>(undefined);
	const reset = editor.reset;
	useEffect(() => {
		if (definitionId === undefined) {
			if (loadedRef.current !== undefined) {
				loadedRef.current = undefined;
				reset(EMPTY_GRAPH);
			}
			return;
		}
		if (loadedKey === undefined || loadedGraph === undefined || loadedRef.current === loadedKey) {
			return;
		}
		loadedRef.current = loadedKey;
		reset(loadedGraph);
	}, [definitionId, loadedGraph, loadedKey, reset]);

	// A stored node with no `position` is laid out on open, and that layout IS an edit (ruling C4) — so a graph nobody
	// has touched opens dirty. Not an error and never auto-saved: the hint just says which kind of unsaved this is.
	const layoutIsUnsaved = (loadedGraph?.nodes ?? []).some((node) => !node.position);

	const serverIssues = validated?.graph === editor.graph ? validated.issues : NO_ISSUES;
	const issues = useMemo(() => [...editor.issues, ...serverIssues], [editor.issues, serverIssues]);

	const selectNode = useCallback(
		(nodeKey: string | undefined) => {
			setSelectedEdgeId(undefined);
			onSelectionChange({ ...selection, nodeKey });
		},
		[onSelectionChange, selection],
	);

	const selectEdge = useCallback(
		(edgeId: string | undefined) => {
			if (edgeId !== undefined && selection.nodeKey !== undefined) {
				onSelectionChange({ ...selection, nodeKey: undefined });
			}
			setSelectedEdgeId(edgeId);
		},
		[onSelectionChange, selection],
	);

	const runValidation = async (graph: GraphWorkflowGraph): Promise<readonly GraphWorkflowGraphIssue[] | undefined> => {
		const result = await validateMutation.mutateAsync({ body: { graph } });
		const found = serverErrorsToIssues(result.errors);
		setValidated({ graph, issues: found });
		return result.valid === true ? undefined : found;
	};

	const handleValidate = (): void => {
		const graph = editor.graph;
		runValidation(graph)
			.then((rejected) => {
				if (rejected === undefined) {
					toast.success(t("pages.graphWorkflows.page.validationPassed", "This graph passed validation."));
				}
			})
			.catch((error: unknown) => {
				toast.error(apiErrorMessage(error, t("pages.graphWorkflows.page.validationFailed", "The graph could not be checked.")));
			});
	};

	const canSave = definition?.id !== undefined && editor.isDirty && editor.issues.length === 0;
	const isSaving = validateMutation.isPending || updateMutation.isPending;

	const saveGraph = async (): Promise<void> => {
		if (definition?.id === undefined || !canSave) {
			return;
		}
		const graph = editor.graph;
		setSaveConflict(false);
		try {
			// The server is asked BEFORE the write, so a refusal costs no version bump and the errors land on their nodes.
			if ((await runValidation(graph)) !== undefined) {
				return;
			}
			await updateMutation.mutateAsync({
				path: { definitionId: definition.id },
				body: {
					version: definition.version ?? 1,
					name: definition.name ?? "",
					description: definition.description ?? null,
					graph,
				},
			});
			editor.markSaved(graph);
			toast.success(t("pages.graphWorkflows.page.saved", "Workflow saved."));
		} catch (error) {
			if (readGraphWorkflowConflict(error)?.conflictType === graphWorkflowConflictTypes.definitionConflict) {
				setSaveConflict(true);
				return;
			}
			toast.error(apiErrorMessage(error, t("pages.graphWorkflows.page.saveFailed", "The workflow could not be saved.")));
		}
	};

	// After a 409 the canvas holds edits made against a version that no longer exists. Reload drops the load guard and
	// refetches; the effect above then resets the canvas onto whatever the other editor saved.
	const handleReload = (): void => {
		setSaveConflict(false);
		loadedRef.current = undefined;
		definitionQuery.refetch().catch(() => undefined);
	};

	const handleMetaSubmit = (values: { name: string; description: string | null }): void => {
		const mode = metaDialog;
		if (mode === undefined) {
			return;
		}
		const done = (): void => setMetaDialog(undefined);
		const failed = (error: unknown): void => {
			toast.error(apiErrorMessage(error, t("pages.graphWorkflows.page.saveFailed", "The workflow could not be saved.")));
		};
		if (mode === "rename") {
			if (definition?.id === undefined) {
				return;
			}
			updateMutation
				.mutateAsync({
					path: { definitionId: definition.id },
					// No `graph`: a rename is a metadata write, and Rename is offered only on a clean canvas so the version
					// bump it causes cannot reload over an edit.
					body: { version: definition.version ?? 1, name: values.name, description: values.description },
				})
				.then(done)
				.catch(failed);
			return;
		}
		createMutation
			.mutateAsync({
				body: { name: values.name, description: values.description, graph: mode === "saveAs" ? editor.graph : STARTER_GRAPH },
			})
			.then((created) => {
				done();
				if (created.id) {
					onSelectionChange({ definitionId: created.id });
				}
			})
			.catch(failed);
	};

	const handleDelete = (id: string): void => {
		confirm({
			title: t("pages.graphWorkflows.page.deleteTitle", "Delete this workflow?"),
			description: t(
				"pages.graphWorkflows.page.deleteBody",
				"The definition is removed. Runs of it keep their own copy of the graph and stay readable.",
			),
			confirmationText: t("common.delete", "Delete"),
			cancellationText: t("common.cancel", "Cancel"),
		})
			.then(async (confirmed) => {
				if (!confirmed) {
					return;
				}
				setDeleteError(undefined);
				try {
					await deleteMutation.mutateAsync({ path: { definitionId: id } });
					if (id === definitionId) {
						onSelectionChange({});
					}
				} catch (error) {
					setDeleteError(
						readGraphWorkflowConflict(error)?.conflictType === graphWorkflowConflictTypes.definitionConflict
							? t("pages.graphWorkflows.page.deleteBlocked", "A live run still uses this workflow. Cancel that run first.")
							: apiErrorMessage(error, t("pages.graphWorkflows.page.deleteFailed", "The workflow could not be deleted.")),
					);
				}
			})
			.catch(() => undefined);
	};

	// The Start node's own default, as the dialog's seed. A field that does not parse falls back to an empty object
	// rather than blocking the dialog: the operator edits it there anyway.
	const startDefaultInput = useMemo((): unknown => {
		const start = editor.nodes.find((node) => node.data.kind === "Start")?.data;
		if (start?.kind !== "Start" || start.defaultInput === null || start.defaultInput.trim().length === 0) {
			return {};
		}
		try {
			return JSON.parse(start.defaultInput) as unknown;
		} catch {
			return {};
		}
	}, [editor.nodes]);

	const selectedNode = selection.nodeKey === undefined ? undefined : editor.nodes.find((node) => node.id === selection.nodeKey);
	const selectedEdge = selectedEdgeId === undefined ? undefined : editor.edges.find((edge) => edge.id === selectedEdgeId);
	const sourceNode = selectedEdge === undefined ? undefined : editor.nodes.find((node) => node.id === selectedEdge.source)?.data;

	const definitionList = (
		<Stack gap="xs" data-testid="gw-page-definitions">
			<GraphWorkflowDefinitionList
				definitions={definitionsQuery.data?.definitions ?? []}
				selectedId={definitionId}
				isLoading={definitionsQuery.isPending}
				error={definitionsQuery.error}
				onSelect={(id) => onSelectionChange({ definitionId: id })}
				onCreate={() => setMetaDialog("create")}
				onDelete={handleDelete}
			/>
			{deleteError ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="gw-page-delete-error">
					{deleteError}
				</Alert>
			) : null}
		</Stack>
	);

	const toolbar = (
		<>
			<Text size="sm" fw={600} data-testid="gw-page-definition-name">
				{definition?.name ?? ""}
			</Text>
			<Button
				size="xs"
				variant="default"
				disabled={definition === undefined || editor.isDirty}
				onClick={() => setMetaDialog("rename")}
				data-testid="gw-page-rename"
			>
				{t("pages.graphWorkflows.page.rename", "Rename")}
			</Button>
			<Button
				size="xs"
				variant="default"
				loading={validateMutation.isPending}
				disabled={definition === undefined}
				onClick={handleValidate}
				data-testid="gw-page-validate"
			>
				{t("pages.graphWorkflows.page.validate", "Check")}
			</Button>
			<Button
				size="xs"
				loading={isSaving}
				disabled={!canSave || isSaving}
				onClick={() => {
					saveGraph().catch(() => undefined);
				}}
				data-testid="gw-page-save"
			>
				{t("pages.graphWorkflows.page.save", "Save")}
			</Button>
			<Button
				size="xs"
				variant="default"
				disabled={definition === undefined}
				onClick={() => setMetaDialog("saveAs")}
				data-testid="gw-page-save-as"
			>
				{t("pages.graphWorkflows.page.saveAs", "Save as…")}
			</Button>
			<Button
				size="xs"
				variant="light"
				disabled={definition === undefined || editor.isDirty}
				onClick={() => setStartOpened(true)}
				data-testid="gw-page-start-run"
			>
				{t("pages.graphWorkflows.page.startRun", "Start run")}
			</Button>
			{editor.isDirty ? (
				layoutIsUnsaved ? (
					<Text size="xs" c="dimmed" data-testid="gw-page-unsaved-layout">
						{t("pages.graphWorkflows.page.unsavedLayout", "This graph had no saved layout — Save to keep the one on screen.")}
					</Text>
				) : (
					<Text size="xs" c="dimmed" data-testid="gw-page-save-first">
						{t("pages.graphWorkflows.page.saveFirst", "Save first — a run executes the saved graph, not the canvas.")}
					</Text>
				)
			) : null}
		</>
	);

	const centre = (
		<Stack gap="sm" h="100%" style={{ minHeight: 0 }} data-testid="gw-page-editor-pane">
			{saveConflict ? (
				<Alert
					color="yellow"
					variant="light"
					icon={<IconAlertTriangle size={16} />}
					title={t("pages.graphWorkflows.page.conflictTitle", "Saved elsewhere")}
					data-testid="gw-page-save-conflict"
				>
					<Stack gap="xs" align="flex-start">
						<Text size="sm">
							{t(
								"pages.graphWorkflows.page.conflictBody",
								"Someone saved this workflow while you were editing it. Reload to see their version — your edits on this canvas are dropped.",
							)}
						</Text>
						<Button size="xs" variant="light" onClick={handleReload} data-testid="gw-page-reload">
							{t("pages.graphWorkflows.page.reload", "Reload")}
						</Button>
					</Stack>
				</Alert>
			) : null}
			{definitionQuery.isError ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="gw-page-definition-error">
					{apiErrorMessage(
						definitionQuery.error,
						t("pages.graphWorkflows.page.loadFailed", "This workflow could not be loaded."),
					)}
				</Alert>
			) : null}
			<div style={{ flex: 1, minHeight: 0 }}>
				<GraphWorkflowEditorCanvas
					editor={editor}
					selectedNodeKey={selection.nodeKey}
					selectedEdgeId={selectedEdgeId}
					onSelectNode={selectNode}
					onSelectEdge={selectEdge}
					issues={issues}
					toolbar={toolbar}
				/>
			</div>
			<GraphWorkflowValidationStrip
				issues={issues}
				onSelectSubject={(subject) => {
					if (editor.nodes.some((node) => node.id === subject)) {
						selectNode(subject);
						return;
					}
					selectEdge(subject);
				}}
			/>
		</Stack>
	);

	const sidePanel = selectedNode ? (
		<GraphWorkflowNodeConfigPanel
			node={selectedNode.data}
			issues={issues.filter((issue) => issue.subject === selectedNode.id)}
			onChange={(patch) => editor.updateNodeData(selectedNode.id, patch)}
			onRename={(to) => {
				const outcome = editor.renameNode(selectedNode.id, to);
				if (outcome === "ok") {
					selectNode(to);
				}
				return outcome;
			}}
			onRemove={() => {
				editor.removeNode(selectedNode.id);
				selectNode(undefined);
			}}
			tools={toolsQuery.data?.tools ?? []}
			agentOptions={agentOptionsQuery.data ?? []}
			modelOptions={modelOptionsQuery.data ?? []}
		/>
	) : selectedEdge ? (
		<GraphWorkflowEdgeConfigPanel
			edge={selectedEdge}
			sourceNode={sourceNode}
			issues={issues.filter((issue) => issue.subject === selectedEdge.id)}
			onChange={(patch) => editor.updateEdgeData(selectedEdge.id, patch)}
			onRemove={() => {
				editor.removeEdge(selectedEdge.id);
				setSelectedEdgeId(undefined);
			}}
		/>
	) : null;

	const closeSidePanel = (): void => {
		setSelectedEdgeId(undefined);
		if (selection.nodeKey !== undefined) {
			onSelectionChange({ ...selection, nodeKey: undefined });
		}
	};

	const body =
		definitionId === undefined ? (
			<EmptyState
				icon={<IconSitemap size={32} opacity={0.5} />}
				message={t("pages.graphWorkflows.empty.editor", "No workflow is open. Pick one from the list, or create a new one.")}
				data-testid="graph-workflows-empty"
			/>
		) : definitionQuery.isPending ? (
			<Loader size="sm" data-testid="gw-page-definition-loading" />
		) : (
			centre
		);

	return (
		<>
			{isNarrow ? (
				<Stack gap="sm" style={{ flex: 1, minHeight: 0 }} data-testid="gw-page-editor-narrow">
					{definitionList}
					<div style={{ flex: 1, minHeight: 0 }}>{body}</div>
				</Stack>
			) : (
				<div
					data-testid="gw-page-editor-grid"
					style={{
						display: "grid",
						gridTemplateColumns: sidePanel ? "320px minmax(320px, 1fr) minmax(360px, 420px)" : "320px minmax(320px, 1fr)",
						gridTemplateRows: "minmax(0, 1fr)",
						gap: "var(--mantine-spacing-md)",
						flex: 1,
						minHeight: 0,
						overflowX: "auto",
					}}
				>
					<div style={{ minHeight: 0, overflowY: "auto" }}>{definitionList}</div>
					{body}
					{sidePanel ? (
						<Paper withBorder={true} p="sm" style={{ minHeight: 0, overflowY: "auto" }} data-testid="gw-page-config-pane">
							{sidePanel}
						</Paper>
					) : null}
				</div>
			)}

			{/* On a narrow viewport the config panel is the same subtree in a drawer — the panel itself is a plain Stack. */}
			<Drawer
				opened={isNarrow && sidePanel !== null}
				onClose={closeSidePanel}
				position="right"
				size="95%"
				title={t("pages.graphWorkflows.page.configTitle", "Configuration")}
				attributes={{ content: { "data-testid": "gw-page-config-drawer" } }}
			>
				{sidePanel}
			</Drawer>

			<GraphWorkflowDefinitionMetaDialog
				opened={metaDialog !== undefined}
				initial={metaDialog === "rename" ? { name: definition?.name ?? "", description: definition?.description } : undefined}
				title={
					metaDialog === "rename"
						? t("pages.graphWorkflows.page.renameTitle", "Rename this workflow")
						: metaDialog === "saveAs"
							? t("pages.graphWorkflows.page.saveAsTitle", "Save as a new workflow")
							: t("pages.graphWorkflows.page.createTitle", "New workflow")
				}
				submitLabel={
					metaDialog === "rename"
						? t("pages.graphWorkflows.page.rename", "Rename")
						: t("pages.graphWorkflows.page.create", "Create")
				}
				isSubmitting={createMutation.isPending || updateMutation.isPending}
				onSubmit={handleMetaSubmit}
				onClose={() => setMetaDialog(undefined)}
			/>

			{definition?.id !== undefined ? (
				<GraphWorkflowStartRunDialog
					opened={startOpened}
					onClose={() => setStartOpened(false)}
					definition={{ id: definition.id, name: definition.name ?? "", version: definition.version ?? 1 }}
					defaultInput={startDefaultInput}
					isDirty={editor.isDirty}
					onStarted={(runId) => {
						setStartOpened(false);
						onSelectionChange({ definitionId: definition.id, runId, tab: "runs" });
					}}
				/>
			) : null}
		</>
	);
}

// ---------------------------------------------------------------------------------------------------------------
// Run mode
// ---------------------------------------------------------------------------------------------------------------

function GraphWorkflowRunMode({ selection, onSelectionChange, isNarrow }: ModeProps) {
	const { t } = useTranslation();
	const runId = selection.runId ?? "";

	const runQuery = useGraphWorkflowRun(runId);
	// Mounted for its invalidations only: every byte on screen comes from the REST reads above and below.
	useGraphWorkflowRunHub(runId);

	const run = runQuery.data?.run;
	// The run knows which definition it was started from; the selection is only the fallback while the run loads.
	const definitionId = run?.definitionId ?? selection.definitionId;
	const definitionQuery = useGraphWorkflowDefinition(definitionId);
	const runsQuery = useGraphWorkflowRuns(definitionId);
	const definition = definitionQuery.data;

	const canvas = useMemo(
		() =>
			toGraphWorkflowRunCanvas({
				run,
				nodeRuns: runQuery.data?.nodeRuns ?? [],
				// Only passed once it has actually loaded: a pending definition query is not a graph MISMATCH.
				definitionGraph: definition === undefined ? undefined : { graph: definition.graph, graphHash: definition.graphHash },
			}),
		[definition, run, runQuery.data?.nodeRuns],
	);

	// The Pause node's own configuration, read off the definition graph through the same defensive parse the canvas
	// uses — the node run carries the decision, never the prompt or the allowed set.
	const pauseConfig = useMemo(() => {
		if (selection.nodeKey === undefined || definition?.graph === undefined) {
			return undefined;
		}
		const node = graphToCanvas(definition.graph).nodes.find((candidate) => candidate.id === selection.nodeKey)?.data;
		return node?.kind === "Pause"
			? { prompt: node.prompt, allowedDecisions: node.allowedDecisions, requireComment: node.requireComment }
			: undefined;
	}, [definition?.graph, selection.nodeKey]);

	const select = useCallback(
		(next: Partial<GraphWorkflowSelection>) => onSelectionChange({ ...selection, ...next }),
		[onSelectionChange, selection],
	);

	const tab = selection.tab === "events" ? "events" : "runs";

	const main = runQuery.isPending ? (
		<Loader size="sm" data-testid="gw-page-run-loading" />
	) : runQuery.isError || run === undefined ? (
		<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="gw-page-run-error">
			<Stack gap="sm" align="flex-start">
				<Text size="sm">
					{apiErrorMessage(runQuery.error, t("pages.graphWorkflows.page.runLoadFailed", "This run could not be loaded."))}
				</Text>
				<Button
					size="xs"
					variant="light"
					onClick={() => select({ runId: undefined, nodeKey: undefined, tab: "editor" })}
					data-testid="gw-page-run-back"
				>
					{t("pages.graphWorkflows.page.backToEditor", "Back to the editor")}
				</Button>
			</Stack>
		</Alert>
	) : (
		<Stack gap="sm" h="100%" style={{ minHeight: 0 }} data-testid="gw-page-run-pane">
			<GraphWorkflowRunToolbar
				run={run}
				onBackToEditor={() => onSelectionChange({ definitionId: definitionId ?? selection.definitionId, tab: "editor" })}
			/>
			<Tabs
				value={tab}
				onChange={(value) => select({ tab: value === "events" ? "events" : "runs" })}
				style={{ flex: 1, minHeight: 0, display: "flex", flexDirection: "column" }}
				data-testid="gw-page-run-tabs"
			>
				<Tabs.List>
					<Tabs.Tab value="runs" data-testid="gw-page-tab-runs">
						{t("pages.graphWorkflows.tab.runs", "Runs")}
					</Tabs.Tab>
					<Tabs.Tab value="events" data-testid="gw-page-tab-events">
						{t("pages.graphWorkflows.tab.events", "Events")}
					</Tabs.Tab>
				</Tabs.List>
				<Tabs.Panel
					value="runs"
					pt="xs"
					style={{ flex: 1, minHeight: 0, display: "flex", flexDirection: "column", gap: "var(--mantine-spacing-sm)" }}
				>
					<div style={{ flex: 1, minHeight: 240 }}>
						<GraphWorkflowRunGraphView
							canvas={canvas}
							selectedNodeKey={selection.nodeKey}
							onSelectNode={(nodeKey) => select({ nodeKey })}
						/>
					</div>
					{/* The table is the accessible path through the same rows — a click here and a click on a card are one
					    `nodeKey` change. */}
					<GraphWorkflowNodeRunTable
						nodeRuns={runQuery.data?.nodeRuns ?? []}
						selectedNodeKey={selection.nodeKey}
						onSelectNode={(nodeKey) => select({ nodeKey })}
					/>
				</Tabs.Panel>
				<Tabs.Panel value="events" pt="xs" style={{ flex: 1, minHeight: 0, overflowY: "auto" }}>
					<GraphWorkflowEventsTab runId={runId} />
				</Tabs.Panel>
			</Tabs>
		</Stack>
	);

	const runList = (
		<GraphWorkflowRunList
			runs={runsQuery.data ?? []}
			isLoading={runsQuery.isPending}
			error={runsQuery.error}
			selectedRunId={selection.runId}
			onSelectRun={(next) => select({ runId: next, nodeKey: undefined })}
		/>
	);

	return (
		<>
			{isNarrow ? (
				// Stacked rather than dropped: without the list there is no way to reach another run of this workflow
				// from a phone, and "Back to the editor" is not that.
				<Stack gap="sm" style={{ flex: 1, minHeight: 0 }} data-testid="gw-page-run-narrow">
					{runList}
					<div style={{ flex: 1, minHeight: 0 }}>{main}</div>
				</Stack>
			) : (
				<div
					data-testid="gw-page-run-grid"
					style={{
						display: "grid",
						gridTemplateColumns: "320px minmax(320px, 1fr)",
						gridTemplateRows: "minmax(0, 1fr)",
						gap: "var(--mantine-spacing-md)",
						flex: 1,
						minHeight: 0,
						overflowX: "auto",
					}}
				>
					<div style={{ minHeight: 0, overflowY: "auto" }}>{runList}</div>
					{main}
				</div>
			)}

			<Drawer
				opened={selection.nodeKey !== undefined}
				onClose={() => select({ nodeKey: undefined })}
				position="right"
				size={isNarrow ? "95%" : "45%"}
				title={selection.nodeKey ?? t("pages.graphWorkflows.page.nodeTitle", "Node")}
				attributes={{ content: { "data-testid": "gw-page-node-drawer" } }}
			>
				{selection.nodeKey === undefined ? null : (
					<GraphWorkflowNodePanel
						runId={runId}
						nodeKey={selection.nodeKey}
						runStatus={run?.status}
						pauseConfig={pauseConfig}
						onClose={() => select({ nodeKey: undefined })}
					/>
				)}
			</Drawer>
		</>
	);
}
