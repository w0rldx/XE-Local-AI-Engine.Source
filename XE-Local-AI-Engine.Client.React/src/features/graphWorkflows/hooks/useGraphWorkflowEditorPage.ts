// The editor mode's whole non-visual half: which query feeds which pane, what a Save actually does, and the state
// that is deliberately page-local rather than a search param. `selectedEdgeId` is not linkable (an edge key means
// nothing without the graph that was open), and the server's validation answer is pinned to the graph it was computed
// FOR, so a later edit drops it without an effect.

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { useUnsavedChangesGuard } from "@/core/ui/hooks/useUnsavedChangesGuard";
import { toast } from "@/core/ui/notifications/Toast";
import { graphWorkflowConflictTypes, readGraphWorkflowConflict } from "@/features/graphWorkflows/api/GraphWorkflowConflict";
import { useGraphWorkflowEditor } from "@/features/graphWorkflows/hooks/useGraphWorkflowEditor";
import { graphWorkflowsEqual } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import type { GraphWorkflowGraph, GraphWorkflowSelection } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import {
	type GraphWorkflowGraphIssue,
	serverErrorsToIssues,
	serverWarningsToIssues,
} from "@/features/graphWorkflows/models/GraphWorkflowValidation";
import {
	useCreateGraphWorkflowDefinition,
	useDeleteGraphWorkflowDefinition,
	useGraphWorkflowAgentOptions,
	useGraphWorkflowDefinition,
	useGraphWorkflowDefinitions,
	useGraphWorkflowModelOptions,
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

export type GraphWorkflowMetaDialogMode = "create" | "rename" | "saveAs";

export function useGraphWorkflowEditorPage(
	selection: GraphWorkflowSelection,
	onSelectionChange: (next: GraphWorkflowSelection) => void,
) {
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
	const [metaDialog, setMetaDialog] = useState<GraphWorkflowMetaDialogMode | undefined>(undefined);
	const [startOpened, setStartOpened] = useState(false);
	const [saveConflict, setSaveConflict] = useState(false);
	const [deleteError, setDeleteError] = useState<string | undefined>(undefined);
	// The server's answer, PINNED to the graph it answered about. Any edit mints a new `editor.graph` object, so the
	// stale errors AND warnings drop out on the next render with no effect and no manual clearing.
	const [validated, setValidated] = useState<
		| {
				readonly graph: GraphWorkflowGraph;
				readonly issues: readonly GraphWorkflowGraphIssue[];
				readonly warnings: readonly GraphWorkflowGraphIssue[];
		  }
		| undefined
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

	// CONTENT, not object identity. A successful save invalidates the definition, the refetch answers a new version,
	// and the load-once effect calls `reset` — which mints a fresh `editor.graph` for a document that did not change,
	// so an identity check erased the very warnings that save had just been told about. `graphWorkflowsEqual`
	// normalises both sides, so a real edit still drops the stale answer on the next render.
	const answeredGraph = validated?.graph;
	const answersThisGraph = useMemo(() => graphWorkflowsEqual(answeredGraph, editor.graph), [answeredGraph, editor.graph]);
	// Errors only. This is the list the canvas rings, the config panels and the save gate read, so a warning can never
	// mark a card red or hold a save.
	const serverIssues = answersThisGraph ? (validated?.issues ?? NO_ISSUES) : NO_ISSUES;
	const issues = useMemo(() => [...editor.issues, ...serverIssues], [editor.issues, serverIssues]);
	const serverWarnings = answersThisGraph ? (validated?.warnings ?? NO_ISSUES) : NO_ISSUES;

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

	/** `rejected` is `undefined` when the graph passed; `warned` says whether the same answer carried notes. */
	const runValidation = async (
		graph: GraphWorkflowGraph,
	): Promise<{ readonly rejected?: readonly GraphWorkflowGraphIssue[]; readonly warned: boolean }> => {
		const result = await validateMutation.mutateAsync({ body: { graph } });
		const found = serverErrorsToIssues(result.errors);
		// Warnings ride along on the same answer and are non-blocking by construction server-side: `valid` is
		// `Errors.Count == 0`, so a graph that only warns still passes and still saves.
		const warnings = serverWarningsToIssues(result.warnings);
		setValidated({ graph, issues: found, warnings });
		return { ...(result.valid === true ? {} : { rejected: found }), warned: warnings.length > 0 };
	};

	const handleValidate = (): void => {
		const graph = editor.graph;
		runValidation(graph)
			.then(({ rejected, warned }) => {
				if (rejected !== undefined) {
					return;
				}
				// "Passed validation" over a strip full of notes reads as "nothing to see"; the neutral sentence sends
				// the operator to them instead.
				toast.success(
					warned
						? t(
								"pages.graphWorkflows.page.validationPassedWithWarnings",
								"This graph is valid. Check the notes below before you run it.",
							)
						: t("pages.graphWorkflows.page.validationPassed", "This graph passed validation."),
				);
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
			if ((await runValidation(graph)).rejected !== undefined) {
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

	const closeSidePanel = (): void => {
		setSelectedEdgeId(undefined);
		if (selection.nodeKey !== undefined) {
			onSelectionChange({ ...selection, nodeKey: undefined });
		}
	};

	return {
		editor,
		definitionId,
		definitionsQuery,
		definitionQuery,
		definition,
		toolsQuery,
		agentOptionsQuery,
		modelOptionsQuery,
		createMutation,
		updateMutation,
		validateMutation,
		selectedEdgeId,
		setSelectedEdgeId,
		metaDialog,
		setMetaDialog,
		startOpened,
		setStartOpened,
		saveConflict,
		deleteError,
		layoutIsUnsaved,
		issues,
		serverWarnings,
		selectNode,
		selectEdge,
		handleValidate,
		canSave,
		isSaving,
		saveGraph,
		handleReload,
		handleMetaSubmit,
		handleDelete,
		startDefaultInput,
		selectedNode,
		selectedEdge,
		sourceNode,
		closeSidePanel,
	};
}
