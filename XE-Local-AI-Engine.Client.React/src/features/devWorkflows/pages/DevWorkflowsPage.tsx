import { Alert, Badge, Button, Card, Group, SimpleGrid, Skeleton, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconPlus, IconSitemap } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { toast } from "@/core/ui/notifications/Toast";
import { type CreateWorkItemValues, CreateWorkItemDialog } from "@/features/devWorkflows/components/CreateWorkItemDialog";
import { DevWorkflowRunStatusBadge, DevWorkflowWorkItemStatusBadge } from "@/features/devWorkflows/components/DevWorkflowStatusBadge";
import { toDevWorkflowRunStatus, toDevWorkflowWorkItemStatus } from "@/features/devWorkflows/models/DevWorkflowModels";
import {
	useCreateDevWorkflowWorkItem,
	useDevelopmentProjectOptions,
	useDevWorkflowDefinitions,
	useDevWorkflowWorkItems,
	useStartDevWorkflowRun,
} from "@/features/devWorkflows/queries/useDevWorkflows";

export function DevWorkflowsPage() {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const [dialogOpened, setDialogOpened] = useState(false);
	const [createError, setCreateError] = useState<string | undefined>(undefined);

	// The list polls itself at 5s while any listed run is live (X16 Q7) — the rule lives in the query hook.
	const listQuery = useDevWorkflowWorkItems();
	const definitionsQuery = useDevWorkflowDefinitions();
	const projectsQuery = useDevelopmentProjectOptions();
	const createMutation = useCreateDevWorkflowWorkItem();
	const startMutation = useStartDevWorkflowRun();

	const workItems = listQuery.data?.items ?? [];

	const handleSubmit = async (values: CreateWorkItemValues): Promise<void> => {
		setCreateError(undefined);
		let workItemId: string;
		try {
			const created = await createMutation.mutateAsync({
				body: { title: values.title, request: values.request, developmentProjectId: values.developmentProjectId ?? null },
			});
			workItemId = created.id ?? "";
		} catch (error) {
			setCreateError(apiErrorMessage(error, t("pages.devWorkflows.create.failed", "Could not create the work item.")));
			return;
		}

		// Two calls, and the item now EXISTS. A failed start must therefore not send the operator back to a form whose
		// resubmit would create a duplicate — the detail page renders the item and offers "start a run" instead.
		try {
			await startMutation.mutateAsync({
				path: { workItemId },
				body: { operationId: crypto.randomUUID(), definitionId: values.definitionId },
			});
		} catch (error) {
			// Toasted rather than swallowed: the detail page shows a work item with no run, which is honest but silent
			// about WHY, and "this graph needs a development project" is exactly the sentence the operator needs to see.
			toast.error(apiErrorMessage(error, t("pages.devWorkflows.detail.startFailed", "Could not start a run.")));
		}
		setDialogOpened(false);
		navigate({ to: "/development-workflows/$workItemId", params: { workItemId } });
	};

	return (
		<PageShell data-testid="dev-workflows-page">
			<PageHeader
				title={t("pages.devWorkflows.title", "Workflow Runs")}
				icon={<IconSitemap size={24} />}
				subtitle={t(
					"pages.devWorkflows.subtitle",
					"Durable development workflows: each work item runs a graph of agent, tool and approval nodes that survives a restart.",
				)}
				actions={
					<Button leftSection={<IconPlus size={16} />} onClick={() => setDialogOpened(true)} data-testid="dev-workflows-create">
						{t("pages.devWorkflows.create.open", "New work item")}
					</Button>
				}
			/>

			{listQuery.isPending ? (
				<SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} data-testid="dev-workflows-loading">
					<Skeleton height={140} radius="md" />
					<Skeleton height={140} radius="md" />
					<Skeleton height={140} radius="md" />
				</SimpleGrid>
			) : listQuery.isError ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflows-error">
					<Stack gap="sm" align="flex-start">
						<Text size="sm">
							{apiErrorMessage(listQuery.error, t("pages.devWorkflows.loadFailed", "Could not load the work items."))}
						</Text>
						<Button
							size="xs"
							variant="light"
							onClick={() => {
								listQuery.refetch().catch(() => undefined);
							}}
							data-testid="dev-workflows-retry"
						>
							{t("pages.devWorkflows.retry", "Retry")}
						</Button>
					</Stack>
				</Alert>
			) : workItems.length === 0 ? (
				<Alert color="blue" variant="light" data-testid="dev-workflows-empty">
					<Stack gap="sm" align="flex-start">
						<Text size="sm">
							{t(
								"pages.devWorkflows.empty",
								"No work items yet. Describe what you want built and pick a workflow template to run it.",
							)}
						</Text>
						<Button size="xs" onClick={() => setDialogOpened(true)} data-testid="dev-workflows-empty-create">
							{t("pages.devWorkflows.createFirst", "Create your first work item")}
						</Button>
					</Stack>
				</Alert>
			) : (
				<SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} data-testid="dev-workflows-list">
					{workItems.map((item) => (
						<Card
							key={item.id}
							withBorder={true}
							padding="md"
							data-testid={`dev-workflow-card-${item.id}`}
							onClick={() => {
								navigate({ to: "/development-workflows/$workItemId", params: { workItemId: item.id ?? "" } });
							}}
							style={{ cursor: "pointer" }}
						>
							<Stack gap="xs">
								<Text fw={600} lineClamp={2}>
									{item.title}
								</Text>
								<Group gap="xs" wrap="wrap">
									<DevWorkflowWorkItemStatusBadge
										status={toDevWorkflowWorkItemStatus(item.status)}
										testId={`dev-workflow-card-status-${item.id}`}
									/>
									{item.latestRunStatus ? (
										<DevWorkflowRunStatusBadge
											status={toDevWorkflowRunStatus(item.latestRunStatus)}
											testId={`dev-workflow-card-run-status-${item.id}`}
										/>
									) : null}
									{item.definitionName ? (
										<Badge size="sm" variant="light" color="gray">
											{item.definitionName}
										</Badge>
									) : null}
								</Group>
								{/* Queued and running are counted separately on purpose (O9). "4 in progress" would imply four
								    agents on one GPU; the node has one agent slot, so most of them are waiting for it. */}
								<Text size="xs" c="dimmed" data-testid={`dev-workflow-card-counts-${item.id}`}>
									{t("pages.devWorkflows.card.counts", "{{running}} running · {{queued}} queued · {{completed}}/{{total}} done", {
										running: item.runningNodeCount ?? 0,
										queued: item.queuedNodeCount ?? 0,
										completed: item.completedNodeCount ?? 0,
										total: item.totalNodeCount ?? 0,
									})}
								</Text>
								<Text size="xs" c="dimmed">
									{t("pages.devWorkflows.card.updated", "updated {{updated}}", {
										updated: new Date(item.updatedAtUtc ?? 0).toLocaleString(),
									})}
								</Text>
							</Stack>
						</Card>
					))}
				</SimpleGrid>
			)}

			<CreateWorkItemDialog
				opened={dialogOpened}
				definitions={definitionsQuery.data?.items ?? []}
				projects={(projectsQuery.data?.items ?? []).map((project) => ({
					id: project.id ?? "",
					// A Dev Mode project has no name of its own — its objective is what identifies it in that surface too.
					label: project.objective ?? project.id ?? "",
				}))}
				isSubmitting={createMutation.isPending || startMutation.isPending}
				errorMessage={createError}
				onClose={() => {
					setCreateError(undefined);
					createMutation.reset();
					startMutation.reset();
					setDialogOpened(false);
				}}
				onSubmit={(values) => {
					handleSubmit(values).catch(() => undefined);
				}}
			/>
		</PageShell>
	);
}
