import { Alert, Badge, Button, Group, Loader, Stack, Switch, Table, Text, TextInput } from "@mantine/core";
import { IconAlertTriangle, IconArchive, IconPlus } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { readDevWorkflowConflict } from "@/features/devWorkflows/api/DevWorkflowConflict";
import { DevWorkflowDefinitionEdgeRow } from "@/features/devWorkflows/components/DevWorkflowDefinitionFormPanel/DevWorkflowDefinitionEdgeRow";
import { DevWorkflowDefinitionNodeCard } from "@/features/devWorkflows/components/DevWorkflowDefinitionFormPanel/DevWorkflowDefinitionNodeCard";
import { toRow, useDevWorkflowDefinitionDraft } from "@/features/devWorkflows/hooks/useDevWorkflowDefinitionDraft";
import {
	useDevWorkflowAgentOptions,
	useDevWorkflowDefinition,
	useDevWorkflowDefinitionMutations,
	useDevWorkflowModelOptions,
} from "@/features/devWorkflows/queries/useDevWorkflows";

export interface DevWorkflowDefinitionFormPanelProps {
	readonly definitionId: string | undefined;
}

/**
 * The definition EDITOR (P4 §2.9, D row): a form over the stored graph document, deliberately not a canvas (N1/X16).
 *
 * Editing is scoped to the fields an operator authors — the node's identity, its binding to an agent, its per-node
 * model and effort overrides, its retry budget and target, and the edges between nodes. `toolMode`,
 * `materialization` and `requiredCapabilities` are shown as read-only badges and ROUND-TRIPPED untouched: the wire
 * DTO is a field-for-field mirror of the stored document, so a definition read back and saved keeps every field it
 * arrived with, and a form that dropped one would quietly delete authoring the runtime depends on.
 *
 * `modelProfile` and `reasoningEffort` ARE dispatched on, and only on an Agent node: that lane creates and resumes the
 * node's work session pinned to them, over the bound agent's own configuration. The model picker offers this node's
 * installed CHAT models, the same list the chat picker uses. A graph may still name one that is gone by the time it
 * runs, and that fails the node run the way a stale pin on an agent definition does — nothing is silently swapped.
 *
 * There is no "new definition" here. The seeder skips slugs that already exist, so an edited template survives a
 * restart, and creating a graph from an empty form is a job for a canvas that D does not ship.
 */
export function DevWorkflowDefinitionFormPanel({ definitionId }: DevWorkflowDefinitionFormPanelProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const definitionQuery = useDevWorkflowDefinition(definitionId);
	const { update, archive } = useDevWorkflowDefinitionMutations();
	const agentOptions = useDevWorkflowAgentOptions();
	const modelOptions = useDevWorkflowModelOptions();

	const definition = definitionQuery.data;
	const {
		name,
		setName,
		nodeRows,
		setNodeRows,
		edgeRows,
		setEdgeRows,
		allowUngatedWrites,
		setAllowUngatedWrites,
		saveError,
		setSaveError,
		isConflict,
		setIsConflict,
		nodeKeyOptions,
		graph,
		issues,
		patchNode,
		moveNode,
		patchEdge,
	} = useDevWorkflowDefinitionDraft(definition);

	const handleSave = async (): Promise<void> => {
		setSaveError(undefined);
		setIsConflict(false);
		try {
			await update.mutateAsync({
				path: { definitionId: definitionId ?? "" },
				// The version this edit was made from (X5). Without it the PUT is a last-writer-wins overwrite.
				body: { version: definition?.version ?? 0, name: name.trim(), graph },
			});
		} catch (error) {
			if (readDevWorkflowConflict(error)) {
				setIsConflict(true);
				return;
			}
			// Everything else is the server's own refusal — a graph rule this form does not mirror, most likely — so its
			// problem detail is rendered verbatim rather than replaced with a sentence that names no field.
			setSaveError(apiErrorMessage(error, t("pages.devWorkflows.definition.saveFailed", "Could not save this template.")));
		}
	};

	const handleArchive = async (): Promise<void> => {
		const confirmed = await confirm({
			title: t("pages.devWorkflows.definition.archiveTitle", "Archive this template?"),
			description: t(
				"pages.devWorkflows.definition.archiveDescription",
				"It disappears from the picker and no new run can start on it. Runs that already pinned a snapshot of it are untouched.",
			),
			confirmationText: t("pages.devWorkflows.definition.archiveConfirm", "Archive"),
			cancellationText: t("common.cancel", "Cancel"),
		});
		if (!confirmed) {
			return;
		}
		setSaveError(undefined);
		try {
			await archive.mutateAsync({ path: { definitionId: definitionId ?? "" } });
		} catch (error) {
			setSaveError(apiErrorMessage(error, t("pages.devWorkflows.definition.archiveFailed", "Could not archive this template.")));
		}
	};

	if (!definitionId) {
		return (
			<EmptyState
				message={t("pages.devWorkflows.definition.pickToEdit", "Pick a template to edit the nodes and edges it runs.")}
				data-testid="dev-workflow-definition-form-empty"
			/>
		);
	}
	if (definitionQuery.isPending) {
		return <Loader size="sm" data-testid="dev-workflow-definition-form-loading" />;
	}
	if (definitionQuery.isError) {
		return (
			<InlineErrorAlert
				variant="light"
				message={apiErrorMessage(
					definitionQuery.error,
					t("pages.devWorkflows.definition.loadFailed", "Could not load this template."),
				)}
				data-testid="dev-workflow-definition-form-error"
			/>
		);
	}

	return (
		<Stack gap="md" data-testid="dev-workflow-definition-form">
			<Group gap="xs" align="flex-end" wrap="wrap">
				<TextInput
					label={t("pages.devWorkflows.definition.nameLabel", "Name")}
					value={name}
					required={true}
					style={{ flex: 1, minWidth: 240 }}
					onChange={(event) => setName(event.currentTarget.value)}
					data-testid="dev-workflow-definition-name"
				/>
				<Badge size="sm" variant="light" color="gray" data-testid="dev-workflow-definition-form-version">
					{t("pages.devWorkflows.definition.version", "v{{version}}", { version: definition?.version ?? 1 })}
				</Badge>
				{/* The waiver is the TEMPLATE saying once, and in writing, that a node here may write to the repository
				    with no operator asked — rather than each node quietly opting itself out. */}
				<Switch
					label={t("pages.devWorkflows.definition.allowUngatedWrites", "Allow ungated writes")}
					description={t(
						"pages.devWorkflows.definition.allowUngatedWritesHelp",
						"Lets a node in this template write to the repository without a human gate on every path into it.",
					)}
					checked={allowUngatedWrites}
					onChange={(event) => setAllowUngatedWrites(event.currentTarget.checked)}
					data-testid="dev-workflow-definition-allow-ungated-writes"
				/>
				<Button
					variant="light"
					color="red"
					leftSection={<IconArchive size={16} />}
					loading={archive.isPending}
					onClick={() => {
						handleArchive().catch(() => undefined);
					}}
					data-testid="dev-workflow-definition-archive"
				>
					{t("pages.devWorkflows.definition.archive", "Archive")}
				</Button>
				<Button
					loading={update.isPending}
					disabled={issues.length > 0 || name.trim().length === 0}
					onClick={() => {
						handleSave().catch(() => undefined);
					}}
					data-testid="dev-workflow-definition-save"
				>
					{t("common.save", "Save")}
				</Button>
			</Group>

			{isConflict ? (
				<Alert
					color="orange"
					variant="light"
					icon={<IconAlertTriangle size={16} />}
					data-testid="dev-workflow-definition-conflict"
				>
					<Stack gap="sm" align="flex-start">
						<Text size="sm">
							{t(
								"pages.devWorkflows.definition.conflict",
								"This template changed elsewhere. Reload it to edit the current version — saving over it would discard that change.",
							)}
						</Text>
						<Button
							size="xs"
							variant="light"
							onClick={() => {
								definitionQuery.refetch().catch(() => undefined);
							}}
							data-testid="dev-workflow-definition-reload"
						>
							{t("pages.devWorkflows.definition.reload", "Reload")}
						</Button>
					</Stack>
				</Alert>
			) : null}

			{saveError ? (
				<InlineErrorAlert variant="light" message={saveError} data-testid="dev-workflow-definition-save-error" />
			) : null}

			{/* Checked BEFORE the save, because a 400 from the graph parser names the rule and not the row. */}
			{issues.length > 0 ? (
				<InlineErrorAlert
					variant="light"
					data-testid="dev-workflow-definition-issues"
					message={
						<Stack gap={4}>
							{issues.map((issue) => (
								<Text
									key={`${issue.rule}:${issue.subject}`}
									size="sm"
									data-testid={`dev-workflow-definition-issue-${issue.rule}`}
								>
									{t(`pages.devWorkflows.definition.issues.${issue.rule}`, issue.rule, { subject: issue.subject })}
								</Text>
							))}
						</Stack>
					}
				/>
			) : null}

			<Stack gap="xs">
				<Group justify="space-between" wrap="wrap">
					<Text fw={600}>{t("pages.devWorkflows.definition.nodes", "Nodes")}</Text>
					<Button
						size="xs"
						variant="light"
						leftSection={<IconPlus size={14} />}
						onClick={() => setNodeRows((current) => [...current, toRow({ nodeKey: "", nodeType: "Agent", label: "" })])}
						data-testid="dev-workflow-definition-add-node"
					>
						{t("pages.devWorkflows.definition.addNode", "Add node")}
					</Button>
				</Group>
				{nodeRows.length === 0 ? (
					<EmptyState
						message={t("pages.devWorkflows.definition.noNodes", "This template has no nodes yet.")}
						data-testid="dev-workflow-definition-no-nodes"
					/>
				) : (
					nodeRows.map((row, index) => (
						<DevWorkflowDefinitionNodeCard
							key={row.id}
							node={row.value}
							index={index}
							nodeCount={nodeRows.length}
							nodeKeyOptions={nodeKeyOptions}
							agentOptions={agentOptions.data ?? []}
							modelOptions={modelOptions.data ?? []}
							onPatch={(patch) => patchNode(row.id, patch)}
							onMove={(offset) => moveNode(row.id, offset)}
							onRemove={() => setNodeRows((current) => current.filter((candidate) => candidate.id !== row.id))}
						/>
					))
				)}
			</Stack>

			<Stack gap="xs">
				<Group justify="space-between" wrap="wrap">
					<Text fw={600}>{t("pages.devWorkflows.definition.edges", "Edges")}</Text>
					<Button
						size="xs"
						variant="light"
						leftSection={<IconPlus size={14} />}
						onClick={() => setEdgeRows((current) => [...current, toRow({ from: "", to: "" })])}
						data-testid="dev-workflow-definition-add-edge"
					>
						{t("pages.devWorkflows.definition.addEdge", "Add edge")}
					</Button>
				</Group>
				{edgeRows.length === 0 ? (
					<EmptyState
						message={t("pages.devWorkflows.definition.noEdges", "This template has no edges yet.")}
						data-testid="dev-workflow-definition-no-edges"
					/>
				) : (
					<Table.ScrollContainer minWidth={720}>
						<Table data-testid="dev-workflow-definition-edges">
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("pages.devWorkflows.definition.edgeFrom", "From")}</Table.Th>
									<Table.Th>{t("pages.devWorkflows.definition.edgeTo", "To")}</Table.Th>
									<Table.Th>{t("pages.devWorkflows.definition.conditionPath", "Condition path")}</Table.Th>
									<Table.Th>{t("pages.devWorkflows.definition.conditionOp", "Operator")}</Table.Th>
									<Table.Th>{t("pages.devWorkflows.definition.conditionValue", "Value")}</Table.Th>
									<Table.Th />
								</Table.Tr>
							</Table.Thead>
							<Table.Tbody>
								{edgeRows.map((row, index) => (
									<DevWorkflowDefinitionEdgeRow
										key={row.id}
										edge={row.value}
										index={index}
										nodeKeyOptions={nodeKeyOptions}
										onPatch={(patch) => patchEdge(row.id, patch)}
										onRemove={() => setEdgeRows((current) => current.filter((candidate) => candidate.id !== row.id))}
									/>
								))}
							</Table.Tbody>
						</Table>
					</Table.ScrollContainer>
				)}
			</Stack>
		</Stack>
	);
}
