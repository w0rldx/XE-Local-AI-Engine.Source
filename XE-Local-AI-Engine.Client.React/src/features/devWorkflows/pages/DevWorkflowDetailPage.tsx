import { ActionIcon, Alert, Anchor, Drawer, Group, Loader, Menu, Stack, Tabs, Text, Tooltip } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import {
	IconAlertTriangle,
	IconDotsVertical,
	IconLayoutSidebar,
	IconLayoutSidebarRight,
	IconTrash,
} from "@tabler/icons-react";
import { Link, useNavigate } from "@tanstack/react-router";
import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { TWO_PANE_BREAKPOINT } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { FullHeightPage } from "@/core/ui/components/FullHeightPage/FullHeightPage";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { DevWorkflowArtifactsTab } from "@/features/devWorkflows/components/DevWorkflowArtifactsTab";
import { DevWorkflowEventsTab } from "@/features/devWorkflows/components/DevWorkflowEventsTab";
import { DevWorkflowNodeRunTable } from "@/features/devWorkflows/components/DevWorkflowNodeRunTable";
import { DevWorkflowRunSummaryPanel } from "@/features/devWorkflows/components/DevWorkflowRunSummaryPanel";
import { DevWorkflowRunToolbar } from "@/features/devWorkflows/components/DevWorkflowRunToolbar";
import { DevWorkflowWorkItemStatusBadge } from "@/features/devWorkflows/components/DevWorkflowStatusBadge";
import { useDevWorkflowRunHub } from "@/features/devWorkflows/hooks/useDevWorkflowRunHub";
import {
	type DevWorkflowDetailTab,
	isActiveDevWorkflowRunStatus,
	toDevWorkflowRunStatus,
	toDevWorkflowWorkItemStatus,
} from "@/features/devWorkflows/models/DevWorkflowModels";
import {
	devWorkflowEventsMaxLimit,
	devWorkflowEventsPageSize,
	useDeleteDevWorkflowWorkItem,
	useDevWorkflowArtifacts,
	useDevWorkflowDefinitions,
	useDevWorkflowRun,
	useDevWorkflowRunEvents,
	useDevWorkflowRunLifecycle,
	useDevWorkflowWorkItem,
	useStartDevWorkflowRun,
} from "@/features/devWorkflows/queries/useDevWorkflows";

export interface DevWorkflowDetailSelection {
	readonly run?: string;
	readonly node?: string;
	readonly tab?: DevWorkflowDetailTab;
}

export interface DevWorkflowDetailPageProps {
	readonly workItemId: string;
	readonly selection: DevWorkflowDetailSelection;
	/** Search-param writes are `replace`d by the route adapter: selecting a node must not push a history entry. */
	readonly onSelectionChange: (next: DevWorkflowDetailSelection) => void;
}

export function DevWorkflowDetailPage({ workItemId, selection, onSelectionChange }: DevWorkflowDetailPageProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const navigate = useNavigate();
	const { width } = useWindowDimensions();
	const isMobile = width < TWO_PANE_BREAKPOINT;
	const [summaryDrawerOpened, summaryDrawer] = useDisclosure(false);
	const [sideDrawerOpened, sideDrawer] = useDisclosure(false);
	const [eventsLimit, setEventsLimit] = useState(devWorkflowEventsPageSize);
	const [deleteError, setDeleteError] = useState<string | undefined>(undefined);

	const workItemQuery = useDevWorkflowWorkItem(workItemId);
	// Absent `?run=` means the latest run; an explicit one renders a historical run from its OWN pinned graph snapshot.
	const runId = selection.run ?? workItemQuery.data?.latestRunId ?? undefined;
	const live = useDevWorkflowRunHub(runId, workItemId);

	// Start every feed in parallel with the work-item request, but once that authoritative request has terminally
	// failed, stop subordinate work even if a failed hub subscription has enabled fallback polling.
	const feedsEnabled = !workItemQuery.isError;
	const poll = { pollIntervalMs: feedsEnabled ? live.pollIntervalMs : undefined, enabled: feedsEnabled };

	const runQuery = useDevWorkflowRun(runId, poll);
	const eventsQuery = useDevWorkflowRunEvents(runId, eventsLimit, poll);
	const artifactsQuery = useDevWorkflowArtifacts(runId, poll);
	const definitionsQuery = useDevWorkflowDefinitions();
	const lifecycle = useDevWorkflowRunLifecycle(runId, workItemId);
	const startRun = useStartDevWorkflowRun();
	const deleteWorkItem = useDeleteDevWorkflowWorkItem();

	const run = runQuery.data;
	// The hub snapshot paints the status before the run query lands; once it has, the query is the authority.
	const runStatus = toDevWorkflowRunStatus(run?.status ?? live.status);
	const nodes = run?.nodes ?? [];
	const pendingDecisionCount = run?.pendingDecisionCount ?? live.pendingDecisionCount ?? 0;
	const blockingGateNodeRunId = run?.blockingGateNodeRunId ?? live.blockingGateNodeRunId ?? undefined;
	// X14: one live run per work item, so a second start is refused with a 409. The control is simply not offered.
	const canStartRun = Boolean(run === undefined || !isActiveDevWorkflowRunStatus(runStatus));

	const select = useCallback(
		(next: DevWorkflowDetailSelection) => onSelectionChange({ ...selection, ...next }),
		[onSelectionChange, selection],
	);

	const handleDelete = useCallback(async (): Promise<void> => {
		const confirmed = await confirm({
			title: t("pages.devWorkflows.delete.title", "Delete this work item?"),
			description: t(
				"pages.devWorkflows.delete.description",
				"Its runs, their node-runs, their work sessions and every artifact they produced are removed. This cannot be undone.",
			),
			confirmationText: t("pages.devWorkflows.delete.confirm", "Delete"),
			cancellationText: t("common.cancel", "Cancel"),
		});
		if (!confirmed) {
			return;
		}
		setDeleteError(undefined);
		try {
			await deleteWorkItem.mutateAsync({ path: { workItemId } });
		} catch (error) {
			// A live run 409s — cancel it first. The refusal destroys nothing, so the page simply stays put.
			setDeleteError(apiErrorMessage(error, t("pages.devWorkflows.delete.failed", "Could not delete this work item.")));
			return;
		}
		navigate({ to: "/development-workflows" });
	}, [confirm, deleteWorkItem, navigate, t, workItemId]);

	if (workItemQuery.isPending) {
		return (
			<FullHeightPage data-testid="dev-workflow-detail-page">
				<Loader data-testid="dev-workflow-detail-loading" />
			</FullHeightPage>
		);
	}

	if (workItemQuery.isError || !workItemQuery.data) {
		return (
			<FullHeightPage data-testid="dev-workflow-detail-page">
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-detail-error">
					<Stack gap="sm" align="flex-start">
						<Text size="sm">
							{apiErrorMessage(workItemQuery.error, t("pages.devWorkflows.detail.notFound", "This work item could not be loaded."))}
						</Text>
						<Anchor component={Link} to="/development-workflows" size="sm" data-testid="dev-workflow-detail-back">
							{t("pages.devWorkflows.detail.back", "Back to workflow runs")}
						</Anchor>
					</Stack>
				</Alert>
			</FullHeightPage>
		);
	}

	const workItem = workItemQuery.data;
	const labelByNodeRunId = new Map(nodes.map((node) => [node.id ?? "", node.label ?? node.nodeKey ?? ""]));

	const summaryPanel = (
		<DevWorkflowRunSummaryPanel
			request={workItem.request}
			runs={workItem.runs ?? []}
			selectedRunId={runId}
			nodes={nodes}
			pendingDecisionCount={pendingDecisionCount}
			startableDefinitions={canStartRun ? (definitionsQuery.data?.items ?? []) : []}
			isStarting={startRun.isPending}
			startError={
				startRun.isError
					? apiErrorMessage(startRun.error, t("pages.devWorkflows.detail.startFailed", "Could not start a run."))
					: undefined
			}
			onSelectRun={(next) => select({ run: next, node: undefined })}
			onStartRun={(definitionId) => {
				startRun.mutate({ path: { workItemId }, body: { operationId: crypto.randomUUID(), definitionId } });
			}}
		/>
	);

	const sidePanel = (
		<Tabs
			value={selection.tab === "events" ? "events" : "artifacts"}
			onChange={(value) => select({ tab: value === "events" ? "events" : "artifacts" })}
			h="100%"
			style={{ display: "flex", flexDirection: "column", minHeight: 0 }}
			data-testid="dev-workflow-side-tabs"
		>
			<Tabs.List>
				<Tabs.Tab value="artifacts" data-testid="dev-workflow-tab-artifacts">
					{t("pages.devWorkflows.artifacts.title", "Artifacts")}
				</Tabs.Tab>
				<Tabs.Tab value="events" data-testid="dev-workflow-tab-events">
					{t("pages.devWorkflows.events.title", "Events")}
				</Tabs.Tab>
			</Tabs.List>
			<Tabs.Panel value="artifacts" pt="xs" style={{ flex: 1, minHeight: 0, overflowY: "auto" }}>
				{runId ? <DevWorkflowArtifactsTab runId={runId} artifacts={artifactsQuery.data?.items ?? []} /> : null}
			</Tabs.Panel>
			<Tabs.Panel value="events" pt="xs" style={{ flex: 1, minHeight: 0, overflowY: "auto" }}>
				<DevWorkflowEventsTab
					events={eventsQuery.data?.items ?? []}
					labelByNodeRunId={labelByNodeRunId}
					hasMore={eventsQuery.data?.hasMore === true}
					canLoadMore={eventsLimit < devWorkflowEventsMaxLimit}
					onLoadMore={() => setEventsLimit((current) => Math.min(current + devWorkflowEventsPageSize, devWorkflowEventsMaxLimit))}
					onSelectNode={(nodeRunId) => select({ node: nodeRunId })}
				/>
			</Tabs.Panel>
		</Tabs>
	);

	const centrePane = (
		<Stack gap="sm" h="100%" style={{ minHeight: 0 }} data-testid="dev-workflow-centre-pane">
			{runId ? (
				<>
					<DevWorkflowRunToolbar
						status={runStatus}
						definitionName={run?.definitionName ?? undefined}
						pendingDecisionCount={pendingDecisionCount}
						blockingGateNodeRunId={blockingGateNodeRunId}
						liveUpdatesUnavailable={live.connectionState === "unavailable"}
						isCommandPending={lifecycle.pause.isPending || lifecycle.resume.isPending || lifecycle.cancel.isPending}
						commandError={
							lifecycle.cancel.isError || lifecycle.pause.isError || lifecycle.resume.isError
								? t("pages.devWorkflows.detail.commandFailed", "That command could not be sent.")
								: undefined
						}
						onPause={() => lifecycle.pause.mutate({ path: { runId }, body: { operationId: crypto.randomUUID() } })}
						onResume={() => lifecycle.resume.mutate({ path: { runId }, body: { operationId: crypto.randomUUID() } })}
						onCancel={() => lifecycle.cancel.mutate({ path: { runId }, body: { operationId: crypto.randomUUID() } })}
						onJumpToDecision={(nodeRunId) => select({ node: nodeRunId })}
					/>
					<div style={{ flex: 1, minHeight: 0, overflowY: "auto" }}>
						<DevWorkflowNodeRunTable
							nodes={nodes}
							selectedNodeRunId={selection.node}
							onSelect={(nodeRunId) => select({ node: nodeRunId })}
						/>
					</div>
				</>
			) : (
				<Alert color="blue" variant="light" data-testid="dev-workflow-detail-no-run">
					{t("pages.devWorkflows.detail.noRunBody", "Nothing has run for this work item yet. Pick a template and start a run.")}
				</Alert>
			)}
		</Stack>
	);

	return (
		<FullHeightPage data-testid="dev-workflow-detail-page">
			<Stack gap="sm" h="100%" style={{ minHeight: 0 }}>
				<Group gap="xs" wrap="nowrap">
					{isMobile ? (
						<Tooltip label={t("pages.devWorkflows.detail.showSummary", "Show runs and progress")}>
							<ActionIcon
								variant="subtle"
								onClick={summaryDrawer.open}
								aria-label={t("pages.devWorkflows.detail.showSummary", "Show runs and progress")}
								data-testid="dev-workflow-summary-toggle"
							>
								<IconLayoutSidebar size={18} />
							</ActionIcon>
						</Tooltip>
					) : null}
					<Text fw={700} lineClamp={1} style={{ flex: 1, minWidth: 0 }} data-testid="dev-workflow-title">
						{workItem.title}
					</Text>
					<DevWorkflowWorkItemStatusBadge
						status={toDevWorkflowWorkItemStatus(workItem.status)}
						testId="dev-workflow-work-item-status"
					/>
					<Menu position="bottom-end" withinPortal={true}>
						<Menu.Target>
							<ActionIcon
								variant="subtle"
								aria-label={t("pages.devWorkflows.detail.actions", "Work item actions")}
								data-testid="dev-workflow-actions"
							>
								<IconDotsVertical size={18} />
							</ActionIcon>
						</Menu.Target>
						<Menu.Dropdown>
							<Menu.Item
								color="red"
								leftSection={<IconTrash size={14} />}
								onClick={() => {
									handleDelete().catch(() => undefined);
								}}
								data-testid="dev-workflow-delete"
							>
								{t("pages.devWorkflows.delete.open", "Delete")}
							</Menu.Item>
						</Menu.Dropdown>
					</Menu>
					{isMobile ? (
						<Tooltip label={t("pages.devWorkflows.detail.showDetails", "Show artifacts and events")}>
							<ActionIcon
								variant="subtle"
								onClick={sideDrawer.open}
								aria-label={t("pages.devWorkflows.detail.showDetails", "Show artifacts and events")}
								data-testid="dev-workflow-side-toggle"
							>
								<IconLayoutSidebarRight size={18} />
							</ActionIcon>
						</Tooltip>
					) : null}
				</Group>
				{deleteError ? (
					<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="dev-workflow-delete-error">
						{deleteError}
					</Alert>
				) : null}
				{isMobile ? (
					<>
						<div style={{ flex: 1, minHeight: 0 }}>{centrePane}</div>
						<Drawer
							opened={summaryDrawerOpened}
							onClose={summaryDrawer.close}
							position="left"
							size="85%"
							title={t("pages.devWorkflows.detail.runs", "Runs")}
							attributes={{ content: { "data-testid": "dev-workflow-summary-drawer" } }}
						>
							{summaryPanel}
						</Drawer>
						<Drawer
							opened={sideDrawerOpened}
							onClose={sideDrawer.close}
							position="right"
							size="85%"
							title={t("pages.devWorkflows.detail.details", "Details")}
							attributes={{ content: { "data-testid": "dev-workflow-side-drawer" } }}
						>
							{sidePanel}
						</Drawer>
					</>
				) : (
					<div
						data-testid="dev-workflow-detail-grid"
						style={{
							display: "grid",
							gridTemplateColumns: "320px minmax(0, 1fr) minmax(380px, 420px)",
							gridTemplateRows: "minmax(0, 1fr)",
							gap: "var(--mantine-spacing-md)",
							flex: 1,
							minHeight: 0,
						}}
					>
						{summaryPanel}
						{centrePane}
						{sidePanel}
					</div>
				)}
			</Stack>
		</FullHeightPage>
	);
}
