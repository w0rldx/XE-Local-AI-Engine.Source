import { Button, Group, Loader, Paper, Stack, Text } from "@mantine/core";
import { IconPlus, IconSparkles } from "@tabler/icons-react";
import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import { GoldenConversationForm } from "@/features/agents/components/GoldenConversationPanel/GoldenConversationForm";
import { GoldenConversationRow } from "@/features/agents/components/GoldenConversationPanel/GoldenConversationRow";
import { GoldenPendingRow } from "@/features/agents/components/GoldenConversationPanel/GoldenPendingRow";
import type { CreateGoldenConversationRequestDto, GoldenConversation } from "@/features/agents/models/GoldenConversationModels";
import {
	useApproveGolden,
	useCreateGoldenConversation,
	useDeleteGoldenConversation,
	useGoldenConversations,
	useHarvestGolden,
} from "@/features/agents/queries/useGoldenConversations";

interface GoldenConversationPanelProps {
	// The agent whose golden conversation set is managed. Rendered by the parent only when agentManagement is on;
	// it also guards internally so it can never render its surface when the capability is off.
	agentDefinitionId: string;
	agentName: string;
	enabled: boolean;
}

// Per-agent golden conversation management. Lists the agent's golden cases (title + turn count +
// assertion/rubric presence), offers an add form (title + input turns + required/forbidden phrases and/or rubric),
// and delete. Capability-gated under agentManagement — when `enabled` is false it renders nothing. The golden set
// gates promotion: the eval runner replays each case against the candidate playbook action.
export function GoldenConversationPanel({ agentDefinitionId, agentName, enabled }: GoldenConversationPanelProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const [formOpen, setFormOpen] = useState(false);

	const goldenQuery = useGoldenConversations(enabled ? agentDefinitionId : null);
	const createMutation = useCreateGoldenConversation(agentDefinitionId);
	const deleteMutation = useDeleteGoldenConversation(agentDefinitionId);
	const harvestMutation = useHarvestGolden(agentDefinitionId);
	const approveMutation = useApproveGolden(agentDefinitionId);

	const isMutating =
		createMutation.isPending || deleteMutation.isPending || harvestMutation.isPending || approveMutation.isPending;

	const cases = goldenQuery.data ?? [];
	// Harvested candidates awaiting operator review (inert until approved); everything else is the active list.
	const pendingCases = cases.filter((goldenCase) => goldenCase.source === "harvested" && !goldenCase.enabled);
	const activeCases = cases.filter((goldenCase) => !(goldenCase.source === "harvested" && !goldenCase.enabled));

	const closeForm = useCallback(() => setFormOpen(false), []);

	const handleHarvest = useCallback(() => {
		harvestMutation.mutate(undefined, {
			onSuccess: (result) => {
				toast.success(
					t(
						"pages.agents.golden.harvest.success",
						"{{created}} proposed, {{duplicate}} already harvested, {{skipped}} skipped.",
						{ created: result.createdCount, duplicate: result.duplicateCount, skipped: result.skippedCount },
					),
				);
			},
			onError: (error) =>
				toast.error(apiErrorMessage(error, t("pages.agents.golden.errors.harvest", "Could not harvest golden cases."))),
		});
	}, [harvestMutation, t]);

	const handleApprove = useCallback(
		(goldenCase: GoldenConversation) => {
			approveMutation.mutate(goldenCase.id, {
				onError: (error) =>
					toast.error(apiErrorMessage(error, t("pages.agents.golden.errors.approve", "Could not approve the golden case."))),
			});
		},
		[approveMutation, t],
	);

	const handleCreate = useCallback(
		(request: CreateGoldenConversationRequestDto) => {
			createMutation.mutate(request, { onSuccess: closeForm });
		},
		[closeForm, createMutation],
	);

	const handleDelete = useCallback(
		async (goldenCase: GoldenConversation) => {
			const confirmed = await confirm({
				title: t("pages.agents.golden.delete.title", "Delete golden case"),
				description: t("pages.agents.golden.delete.description", "Delete '{{title}}'? This cannot be undone.", {
					title: goldenCase.title,
				}),
				confirmationText: t("common.delete", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (confirmed) {
				deleteMutation.mutate(goldenCase.id, {
					onError: (error) =>
						toast.error(apiErrorMessage(error, t("pages.agents.golden.errors.delete", "Could not delete the golden case."))),
				});
			}
		},
		[confirm, deleteMutation, t],
	);

	if (!enabled) {
		return null;
	}

	const createError = createMutation.error
		? apiErrorMessage(createMutation.error, t("pages.agents.golden.errors.save", "Could not save the golden case."))
		: undefined;

	return (
		<Paper withBorder={true} radius="md" p="md" data-testid={`golden-panel-${agentDefinitionId}`}>
			<Stack gap="sm">
				<Group justify="space-between" align="flex-start">
					<Stack gap={2}>
						<Text fw={600}>{t("pages.agents.golden.title", "Golden conversations")}</Text>
						<Text size="xs" c="dimmed">
							{t(
								"pages.agents.golden.subtitle",
								"Author golden cases that gate promotion: a candidate action must not regress them before {{name}} can enable it.",
								{ name: agentName },
							)}
						</Text>
					</Stack>
					{!formOpen ? (
						<Group gap="xs">
							<Button
								size="xs"
								variant="light"
								leftSection={<IconSparkles size={14} />}
								onClick={handleHarvest}
								loading={harvestMutation.isPending}
								disabled={isMutating}
								data-testid="golden-harvest-button"
							>
								{t("pages.agents.golden.harvest.button", "Harvest from 👍")}
							</Button>
							<Button
								size="xs"
								variant="light"
								leftSection={<IconPlus size={14} />}
								onClick={() => setFormOpen(true)}
								disabled={isMutating}
								data-testid="golden-add-button"
							>
								{t("pages.agents.golden.addButton", "Add golden case")}
							</Button>
						</Group>
					) : null}
				</Group>

				{formOpen ? (
					<GoldenConversationForm
						isSubmitting={createMutation.isPending}
						submitError={createError}
						onSubmit={handleCreate}
						onCancel={closeForm}
					/>
				) : null}

				{goldenQuery.isLoading ? (
					<Group gap="sm" data-testid="golden-loading">
						<Loader size="sm" />
						<Text c="dimmed" size="sm">
							{t("pages.agents.golden.loading", "Loading golden cases…")}
						</Text>
					</Group>
				) : null}

				{goldenQuery.error ? (
					<InlineErrorAlert
						message={apiErrorMessage(goldenQuery.error, t("pages.agents.golden.errors.load", "Could not load golden cases."))}
						data-testid="golden-list-error"
					/>
				) : null}

				{!goldenQuery.isLoading && !goldenQuery.error && cases.length === 0 && !formOpen ? (
					<EmptyState
						size="sm"
						message={t("pages.agents.golden.empty", "No golden cases yet. Add one so promotion can be eval-gated.")}
						data-testid="golden-empty"
					/>
				) : null}

				{pendingCases.length > 0 ? (
					<Stack gap="xs" data-testid="golden-pending-section">
						<Text size="sm" fw={600}>
							{t("pages.agents.golden.pending.heading", "Harvested (pending review)")}
						</Text>
						<Text size="xs" c="dimmed">
							{t(
								"pages.agents.golden.pending.hint",
								"Review each harvested candidate, then approve it into the active golden set or reject it.",
							)}
						</Text>
						{pendingCases.map((goldenCase) => (
							<GoldenPendingRow
								key={goldenCase.id}
								goldenCase={goldenCase}
								disabled={isMutating}
								onApprove={() => handleApprove(goldenCase)}
								onReject={() => handleDelete(goldenCase)}
							/>
						))}
					</Stack>
				) : null}

				{activeCases.map((goldenCase) => (
					<GoldenConversationRow
						key={goldenCase.id}
						goldenCase={goldenCase}
						disabled={isMutating}
						onDelete={() => handleDelete(goldenCase)}
					/>
				))}
			</Stack>
		</Paper>
	);
}
