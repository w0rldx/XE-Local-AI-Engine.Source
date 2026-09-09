import { Alert, Button, Drawer, Loader, Paper, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconSitemap } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { ResponsivePaneLayout } from "@/core/ui/components/ResponsivePaneLayout/ResponsivePaneLayout";
import { GraphWorkflowDefinitionList } from "@/features/graphWorkflows/components/GraphWorkflowDefinitionList";
import { GraphWorkflowDefinitionMetaDialog } from "@/features/graphWorkflows/components/GraphWorkflowDefinitionMetaDialog";
import { GraphWorkflowEdgeConfigPanel } from "@/features/graphWorkflows/components/GraphWorkflowEdgeConfigPanel";
import { GraphWorkflowEditorCanvas } from "@/features/graphWorkflows/components/GraphWorkflowEditorCanvas";
import { GraphWorkflowEditorToolbar } from "@/features/graphWorkflows/components/GraphWorkflowEditorToolbar";
import { GraphWorkflowNodeConfigPanel } from "@/features/graphWorkflows/components/GraphWorkflowNodeConfigPanel";
import { GraphWorkflowStartRunDialog } from "@/features/graphWorkflows/components/GraphWorkflowStartRunDialog";
import { GraphWorkflowValidationStrip } from "@/features/graphWorkflows/components/GraphWorkflowValidationStrip";
import { useGraphWorkflowEditorPage } from "@/features/graphWorkflows/hooks/useGraphWorkflowEditorPage";
import type { GraphWorkflowSelection } from "@/features/graphWorkflows/models/GraphWorkflowModels";

export interface GraphWorkflowEditorModeProps {
	readonly selection: GraphWorkflowSelection;
	readonly onSelectionChange: (next: GraphWorkflowSelection) => void;
	readonly isNarrow: boolean;
}

/** Authoring: the definition list, the canvas and whichever config panel the current selection opens. */
export function GraphWorkflowEditorMode({ selection, onSelectionChange, isNarrow }: GraphWorkflowEditorModeProps) {
	const { t } = useTranslation();
	const {
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
	} = useGraphWorkflowEditorPage(selection, onSelectionChange);

	// The rail scrolls inside its own column rather than stretching the grid row. Harmless in the stacked narrow mode,
	// where the column has no height to overflow.
	const definitionList = (
		<Stack gap="xs" style={{ minHeight: 0, overflowY: "auto" }} data-testid="gw-page-definitions">
			<GraphWorkflowDefinitionList
				definitions={definitionsQuery.data?.definitions ?? []}
				selectedId={definitionId}
				isLoading={definitionsQuery.isPending}
				error={definitionsQuery.error}
				onSelect={(id) => onSelectionChange({ definitionId: id })}
				onCreate={() => setMetaDialog("create")}
				onDelete={handleDelete}
			/>
			{deleteError ? <InlineErrorAlert variant="light" message={deleteError} data-testid="gw-page-delete-error" /> : null}
		</Stack>
	);

	const toolbar = (
		<GraphWorkflowEditorToolbar
			definitionName={definition?.name ?? ""}
			hasDefinition={definition !== undefined}
			isDirty={editor.isDirty}
			layoutIsUnsaved={layoutIsUnsaved}
			isValidating={validateMutation.isPending}
			isSaving={isSaving}
			canSave={canSave}
			onRename={() => setMetaDialog("rename")}
			onValidate={handleValidate}
			onSave={() => {
				saveGraph().catch(() => undefined);
			}}
			onSaveAs={() => setMetaDialog("saveAs")}
			onStartRun={() => setStartOpened(true)}
		/>
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
				<InlineErrorAlert
					variant="light"
					message={apiErrorMessage(
						definitionQuery.error,
						t("pages.graphWorkflows.page.loadFailed", "This workflow could not be loaded."),
					)}
					data-testid="gw-page-definition-error"
				/>
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
				warnings={serverWarnings}
				onSelectSubject={(subject) => {
					if (editor.nodes.some((node) => node.id === subject)) {
						selectNode(subject);
						return;
					}
					// A server issue can name a key the canvas no longer holds. Selecting nothing is the honest answer;
					// `selectEdge` on a missing id used to open an empty drawer over the canvas.
					if (editor.edges.some((edge) => edge.id === subject)) {
						selectEdge(subject);
					}
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
			{/* Stacked rather than dropped: without the definition list there is no way to reach another workflow from a
			    phone, and the toolbar is not that. */}
			<ResponsivePaneLayout
				narrowMode="stack"
				list={definitionList}
				main={body}
				side={
					sidePanel === null ? null : (
						<Paper withBorder={true} p="sm" style={{ minHeight: 0, overflowY: "auto" }} data-testid="gw-page-config-pane">
							{sidePanel}
						</Paper>
					)
				}
				narrowTestId="gw-page-editor-narrow"
				gridTestId="gw-page-editor-grid"
			/>

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
