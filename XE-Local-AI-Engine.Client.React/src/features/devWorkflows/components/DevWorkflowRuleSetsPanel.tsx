import { ActionIcon, Alert, Badge, Button, Group, Loader, Paper, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconPencil, IconPlus, IconTrash } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { readDevWorkflowConflict } from "@/features/devWorkflows/api/DevWorkflowConflict";
import {
	type DevWorkflowProjectOption,
	type DevWorkflowRuleSetValues,
	DevWorkflowRuleSetDialog,
} from "@/features/devWorkflows/components/DevWorkflowRuleSetDialog";
import type { DevWorkflowRuleSetSummaryResponse } from "@/features/devWorkflows/models/DevWorkflowModels";
import {
	useDevWorkflowRuleSet,
	useDevWorkflowRuleSetMutations,
	useDevWorkflowRuleSets,
} from "@/features/devWorkflows/queries/useDevWorkflows";

export interface DevWorkflowRuleSetsPanelProps {
	/** Dev Mode projects, for the `projectIds` scope axis. Read once by the page and handed down. */
	readonly projects: readonly DevWorkflowProjectOption[];
}

/**
 * The rule-set catalogue: the policy documents the resolver injects into a matching node's objective at
 * materialization time (Y2/M4).
 *
 * It lives on the LIST page rather than on a work item because a rule set is scoped by `{ projectIds, nodeTypes }` and
 * by nothing else — it belongs to no run, and an operator writes one BEFORE the workflow that will pick it up exists.
 */
export function DevWorkflowRuleSetsPanel({ projects }: DevWorkflowRuleSetsPanelProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const listQuery = useDevWorkflowRuleSets();
	const { create, update, remove } = useDevWorkflowRuleSetMutations();

	const [editingId, setEditingId] = useState<string | undefined>(undefined);
	const [dialogOpened, setDialogOpened] = useState(false);
	const [saveError, setSaveError] = useState<string | undefined>(undefined);
	const [deleteError, setDeleteError] = useState<string | undefined>(undefined);

	// The body lives on the single-rule-set read, never on a summary: a catalogue of ten rule sets is otherwise forty
	// kilobytes of policy prose fetched to render ten names.
	const editingQuery = useDevWorkflowRuleSet(dialogOpened ? editingId : undefined);

	const closeDialog = (): void => {
		setDialogOpened(false);
		setEditingId(undefined);
		setSaveError(undefined);
		create.reset();
		update.reset();
	};

	const handleSubmit = async (values: DevWorkflowRuleSetValues): Promise<void> => {
		setSaveError(undefined);
		const scope = { projectIds: [...values.projectIds], nodeTypes: [...values.nodeTypes] };
		const body = {
			name: values.name,
			description: values.description.trim() ? values.description : null,
			body: values.body,
			scope,
			enabled: values.enabled,
		};
		try {
			if (editingId) {
				await update.mutateAsync({
					path: { ruleSetId: editingId },
					// The version the edit was made against. Without it the PUT is a last-writer-wins overwrite of
					// whatever landed in between, which is the one thing optimistic concurrency exists to refuse.
					body: { ...body, version: editingQuery.data?.version ?? 0 },
				});
			} else {
				await create.mutateAsync({ body });
			}
		} catch (error) {
			// A 409 is not a failed save to retry — someone else wrote this row, and the form is holding a body that was
			// edited from a version that no longer exists. Reloading is the only honest next step, so it is what the
			// message asks for rather than offering a Save that would 409 again.
			setSaveError(
				readDevWorkflowConflict(error)
					? t(
							"pages.devWorkflows.ruleSets.conflict",
							"This rule set changed elsewhere. Close and reopen it to edit the current version.",
						)
					: apiErrorMessage(error, t("pages.devWorkflows.ruleSets.saveFailed", "Could not save this rule set.")),
			);
			return;
		}
		closeDialog();
	};

	const handleDelete = async (ruleSet: DevWorkflowRuleSetSummaryResponse): Promise<void> => {
		const confirmed = await confirm({
			title: t("pages.devWorkflows.ruleSets.deleteTitle", "Delete this rule set?"),
			description: t(
				"pages.devWorkflows.ruleSets.deleteDescription",
				"Runs that already applied it keep the copy they were given; nothing new will pick it up.",
			),
			confirmationText: t("pages.devWorkflows.ruleSets.deleteConfirm", "Delete"),
			cancellationText: t("common.cancel", "Cancel"),
		});
		if (!confirmed) {
			return;
		}
		setDeleteError(undefined);
		try {
			await remove.mutateAsync({ path: { ruleSetId: ruleSet.id ?? "" } });
		} catch (error) {
			setDeleteError(apiErrorMessage(error, t("pages.devWorkflows.ruleSets.deleteFailed", "Could not delete this rule set.")));
		}
	};

	const ruleSets = listQuery.data?.items ?? [];

	return (
		<Stack gap="sm" data-testid="dev-workflow-rule-sets">
			<Group justify="space-between" wrap="wrap">
				<Text size="sm" c="dimmed">
					{t(
						"pages.devWorkflows.ruleSets.intro",
						"Policy that is written into a matching node's objective when the run reaches it. Scope by project, by node type, or leave both open to apply everywhere.",
					)}
				</Text>
				<Button
					leftSection={<IconPlus size={16} />}
					onClick={() => {
						setEditingId(undefined);
						setSaveError(undefined);
						setDialogOpened(true);
					}}
					data-testid="dev-workflow-rule-set-create"
				>
					{t("pages.devWorkflows.ruleSets.create", "New rule set")}
				</Button>
			</Group>

			{deleteError ? (
				<Alert
					color="red"
					variant="light"
					icon={<IconAlertTriangle size={16} />}
					data-testid="dev-workflow-rule-sets-delete-error"
				>
					{deleteError}
				</Alert>
			) : null}

			{listQuery.isPending ? (
				<Loader size="sm" data-testid="dev-workflow-rule-sets-loading" />
			) : listQuery.isError ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-rule-sets-error">
					<Stack gap="sm" align="flex-start">
						<Text size="sm">
							{apiErrorMessage(listQuery.error, t("pages.devWorkflows.ruleSets.loadFailed", "Could not load the rule sets."))}
						</Text>
						<Button
							size="xs"
							variant="light"
							onClick={() => {
								listQuery.refetch().catch(() => undefined);
							}}
							data-testid="dev-workflow-rule-sets-retry"
						>
							{t("pages.devWorkflows.retry", "Retry")}
						</Button>
					</Stack>
				</Alert>
			) : ruleSets.length === 0 ? (
				<EmptyState
					message={t(
						"pages.devWorkflows.ruleSets.empty",
						"No rule sets yet. Every node runs on its template's instructions alone.",
					)}
					data-testid="dev-workflow-rule-sets-empty"
				/>
			) : (
				<Stack gap="xs" data-testid="dev-workflow-rule-sets-list">
					{ruleSets.map((ruleSet) => (
						<Paper key={ruleSet.id} withBorder={true} p="sm" data-testid={`dev-workflow-rule-set-${ruleSet.id}`}>
							<Group gap="xs" wrap="nowrap" align="flex-start">
								<Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
									<Group gap="xs" wrap="wrap">
										<Text fw={600} lineClamp={1}>
											{ruleSet.name}
										</Text>
										{ruleSet.enabled === false ? (
											<Badge size="xs" variant="light" color="gray" data-testid={`dev-workflow-rule-set-disabled-${ruleSet.id}`}>
												{t("pages.devWorkflows.ruleSets.disabled", "Disabled")}
											</Badge>
										) : null}
									</Group>
									{ruleSet.description ? (
										<Text size="xs" c="dimmed" lineClamp={2}>
											{ruleSet.description}
										</Text>
									) : null}
									<Text size="xs" c="dimmed" data-testid={`dev-workflow-rule-set-scope-${ruleSet.id}`}>
										{scopeSummary(ruleSet, projects, t)}
									</Text>
								</Stack>
								<ActionIcon
									variant="subtle"
									aria-label={t("pages.devWorkflows.ruleSets.edit", "Edit rule set")}
									onClick={() => {
										setEditingId(ruleSet.id);
										setSaveError(undefined);
										setDialogOpened(true);
									}}
									data-testid={`dev-workflow-rule-set-edit-${ruleSet.id}`}
								>
									<IconPencil size={16} />
								</ActionIcon>
								<ActionIcon
									variant="subtle"
									color="red"
									aria-label={t("pages.devWorkflows.ruleSets.delete", "Delete rule set")}
									onClick={() => {
										handleDelete(ruleSet).catch(() => undefined);
									}}
									data-testid={`dev-workflow-rule-set-delete-${ruleSet.id}`}
								>
									<IconTrash size={16} />
								</ActionIcon>
							</Group>
						</Paper>
					))}
				</Stack>
			)}

			<DevWorkflowRuleSetDialog
				opened={dialogOpened}
				ruleSet={editingId ? editingQuery.data : undefined}
				isLoading={Boolean(editingId) && editingQuery.isPending}
				projects={projects}
				isSubmitting={create.isPending || update.isPending}
				errorMessage={saveError}
				onClose={closeDialog}
				onSubmit={(values) => {
					handleSubmit(values).catch(() => undefined);
				}}
			/>
		</Stack>
	);
}

/**
 * What a rule set actually matches, in one line. An EMPTY axis is "every value" and is said so out loud: read as an
 * unset field it looks like a scope the operator forgot to fill in, when it is the widest scope there is.
 */
function scopeSummary(
	ruleSet: DevWorkflowRuleSetSummaryResponse,
	projects: readonly DevWorkflowProjectOption[],
	t: (key: string, fallback: string, options?: Record<string, unknown>) => string,
): string {
	const projectIds = ruleSet.scope?.projectIds ?? [];
	const nodeTypes = ruleSet.scope?.nodeTypes ?? [];
	const projectLabel =
		projectIds.length === 0
			? t("pages.devWorkflows.ruleSets.everyProject", "every project")
			: projectIds.map((id) => projects.find((project) => project.id === id)?.label ?? id).join(", ");
	const nodeTypeLabel =
		nodeTypes.length === 0
			? t("pages.devWorkflows.ruleSets.everyNodeType", "every node type")
			: nodeTypes.map((nodeType) => t(`pages.devWorkflows.nodeType.${nodeType}`, nodeType)).join(", ");
	return t("pages.devWorkflows.ruleSets.scopeSummary", "{{projects}} · {{nodeTypes}}", {
		projects: projectLabel,
		nodeTypes: nodeTypeLabel,
	});
}
