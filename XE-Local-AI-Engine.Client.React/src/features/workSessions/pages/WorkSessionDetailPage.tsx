import { ActionIcon, Alert, Anchor, Badge, Drawer, Group, Loader, Menu, Stack, Text, Tooltip } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconAlertTriangle, IconDotsVertical, IconLayoutSidebar, IconLayoutSidebarRight, IconPencil, IconTrash } from "@tabler/icons-react";
import { Link, useNavigate } from "@tanstack/react-router";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { FullHeightPage } from "@/core/ui/components/FullHeightPage/FullHeightPage";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import type { ChatScope } from "@/features/chat/models/ChatModels";
import { Chat } from "@/features/chat/pages/Chat";
import { EditWorkSessionDialog } from "@/features/workSessions/components/EditWorkSessionDialog";
import { WorkSessionFollowUpNotice } from "@/features/workSessions/components/WorkSessionFollowUpNotice";
import { WorkSessionPlanPanel } from "@/features/workSessions/components/WorkSessionPlanPanel";
import { WorkSessionSidePanel } from "@/features/workSessions/components/WorkSessionSidePanel";
import { useWorkSessionHub } from "@/features/workSessions/hooks/useWorkSessionHub";
import { isTerminalWorkSessionStatus, toWorkSessionKind, toWorkSessionStatus } from "@/features/workSessions/models/WorkSessionModels";
import {
	useDeleteWorkSession,
	usePostWorkSessionMessage,
	useUpdateWorkSession,
	useWorkSession,
	useWorkSessionArtifacts,
	useWorkSessionCheckpoints,
	useWorkSessionEvents,
	useWorkSessionFindings,
	useWorkSessionLifecycle,
	useWorkSessionTasks,
	workSessionEventsMaxLimit,
	workSessionEventsPageSize,
} from "@/features/workSessions/queries/useWorkSessions";

// The app shell keeps a 220px sidebar from 768 up, so a 320 plan + chat + 380 side-panel row has no room to breathe
// below roughly 1024 — the same threshold, for the same reason, that ChatDisplayShell records. jsdom defaults
// innerWidth to 1024, so `< 1024` (not `<=`) keeps the desktop layout the default under the existing tests.
const DESKTOP_MIN_WIDTH = 1024;

export function WorkSessionDetailPage({ sessionId }: { sessionId: string }) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const navigate = useNavigate();
	const { width } = useWindowDimensions();
	const isMobile = width < DESKTOP_MIN_WIDTH;
	const [planDrawerOpened, planDrawer] = useDisclosure(false);
	const [sideDrawerOpened, sideDrawer] = useDisclosure(false);
	const [eventsLimit, setEventsLimit] = useState(workSessionEventsPageSize);
	const [followUpError, setFollowUpError] = useState<string | undefined>(undefined);
	const [editDialogOpened, editDialog] = useDisclosure(false);
	const [deleteError, setDeleteError] = useState<string | undefined>(undefined);

	const sessionQuery = useWorkSession(sessionId);
	const conversationId = sessionQuery.data?.conversationId;
	const live = useWorkSessionHub(sessionId, conversationId);
	// One cadence for every query on the page: the hub reports it while it is down, and nothing polls while it is up.
	const poll = { pollIntervalMs: live.pollIntervalMs };

	const tasksQuery = useWorkSessionTasks(sessionId, poll);
	const findingsQuery = useWorkSessionFindings(sessionId, poll);
	const artifactsQuery = useWorkSessionArtifacts(sessionId, poll);
	const checkpointsQuery = useWorkSessionCheckpoints(sessionId, poll);
	const eventsQuery = useWorkSessionEvents(sessionId, eventsLimit, poll);
	const lifecycle = useWorkSessionLifecycle(sessionId);
	const postMessage = usePostWorkSessionMessage();
	const updateSession = useUpdateWorkSession(sessionId);
	const deleteSession = useDeleteWorkSession();

	// The snapshot paints the header before the detail query lands; once it has, the query is the authority.
	const status = toWorkSessionStatus(sessionQuery.data?.status ?? live.status);
	const stepCount = sessionQuery.data?.stepCount ?? live.step ?? 0;
	const currentTaskId = sessionQuery.data?.currentTaskId ?? live.currentTaskId;

	const events = eventsQuery.data?.items ?? [];
	const checkpoints = checkpointsQuery.data?.items ?? [];
	const latestCheckpointStep = checkpoints.reduce<number | undefined>(
		(highest, checkpoint) => (highest === undefined || (checkpoint.step ?? 0) > highest ? (checkpoint.step ?? 0) : highest),
		undefined,
	);
	const lastFailureOutcome = events
		.slice()
		.sort((left, right) => (right.sequence ?? 0) - (left.sequence ?? 0))
		.find((event) => Boolean(event.outcome))?.outcome;

	const handleSendFollowUp = useCallback(
		async (text: string): Promise<void> => {
			setFollowUpError(undefined);
			try {
				await postMessage.mutateAsync({ path: { sessionId }, body: { text } });
			} catch (error) {
				setFollowUpError(apiErrorMessage(error, t("pages.workSessions.followUp.failed", "Could not send that message.")));
				// Rethrown so the composer keeps the draft — a rejected follow-up must stay on screen to retry.
				throw error;
			}
			await sessionQuery.refetch();
		},
		[postMessage, sessionId, sessionQuery, t],
	);

	const handlePause = useCallback(() => {
		lifecycle.pause.mutate({ path: { sessionId } });
	}, [lifecycle.pause, sessionId]);

	const agentDefinitionId = sessionQuery.data?.agentDefinitionId;
	const handleSaveEdits = useCallback(
		(values: { title: string; objective: string }) => {
			if (!agentDefinitionId) {
				return;
			}
			// PATCH, but the generated request types all three fields as required, so the unchanged agent rides along.
			updateSession.mutate(
				{ path: { sessionId }, body: { ...values, agentDefinitionId } },
				{ onSuccess: () => editDialog.close() },
			);
		},
		[agentDefinitionId, editDialog, sessionId, updateSession],
	);

	const handleDelete = useCallback(async (): Promise<void> => {
		const confirmed = await confirm({
			title: t("pages.workSessions.delete.title", "Delete this work session?"),
			description: t(
				"pages.workSessions.delete.description",
				"Its plan, findings, artifacts, checkpoints and its whole conversation are removed. This cannot be undone.",
			),
			confirmationText: t("pages.workSessions.delete.confirm", "Delete"),
			cancellationText: t("common.cancel", "Cancel"),
		});
		if (!confirmed) {
			return;
		}
		setDeleteError(undefined);
		try {
			await deleteSession.mutateAsync({ path: { sessionId } });
		} catch (error) {
			// A running session 409s — cancel it first. Never leave the route open on a half-deleted session.
			setDeleteError(apiErrorMessage(error, t("pages.workSessions.delete.failed", "Could not delete this work session.")));
			return;
		}
		// The delete also removes the OWNED conversation, so this route must not stay open on it.
		navigate({ to: "/work-sessions" });
	}, [confirm, deleteSession, navigate, sessionId, t]);

	const scope = useMemo<ChatScope | undefined>(
		() =>
			conversationId
				? {
						conversationId,
						pinnedAgentId: agentDefinitionId,
						resumeNonce: live.resumeNonce,
						onSendOverride: handleSendFollowUp,
						onStopOverride: handlePause,
						composerDisabled: isTerminalWorkSessionStatus(status),
						embedded: true,
					}
				: undefined,
		[conversationId, agentDefinitionId, live.resumeNonce, handleSendFollowUp, handlePause, status],
	);

	if (sessionQuery.isPending) {
		return (
			<FullHeightPage data-testid="work-session-detail-page">
				<Loader data-testid="work-session-detail-loading" />
			</FullHeightPage>
		);
	}

	if (sessionQuery.isError || !sessionQuery.data) {
		return (
			<FullHeightPage data-testid="work-session-detail-page">
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="work-session-detail-error">
					<Stack gap="sm" align="flex-start">
						<Text size="sm">
							{apiErrorMessage(sessionQuery.error, t("pages.workSessions.detail.notFound", "This work session could not be loaded."))}
						</Text>
						<Anchor component={Link} to="/work-sessions" size="sm" data-testid="work-session-detail-back">
							{t("pages.workSessions.detail.back", "Back to work sessions")}
						</Anchor>
					</Stack>
				</Alert>
			</FullHeightPage>
		);
	}

	const planPanel = (
		<WorkSessionPlanPanel
			status={status}
			stepCount={stepCount}
			maxStepsPerRun={sessionQuery.data.maxStepsPerRun ?? 0}
			currentTaskId={currentTaskId}
			tasks={tasksQuery.data?.items ?? []}
			isLoadingTasks={tasksQuery.isPending}
			latestCheckpointStep={latestCheckpointStep}
			lastFailureOutcome={lastFailureOutcome ?? undefined}
			liveUpdatesUnavailable={live.connectionState === "unavailable"}
			isCommandPending={
				lifecycle.start.isPending || lifecycle.pause.isPending || lifecycle.resume.isPending || lifecycle.cancel.isPending
			}
			onStart={() => lifecycle.start.mutate({ path: { sessionId } })}
			onPause={handlePause}
			onResume={() => lifecycle.resume.mutate({ path: { sessionId } })}
			onCancel={() => lifecycle.cancel.mutate({ path: { sessionId } })}
		/>
	);

	const sidePanel = (
		<WorkSessionSidePanel
			sessionId={sessionId}
			status={status}
			findings={findingsQuery.data?.items ?? []}
			artifacts={artifactsQuery.data?.items ?? []}
			checkpoints={checkpoints}
			events={events}
			hasMoreEvents={eventsQuery.data?.hasMore === true}
			canLoadMoreEvents={eventsLimit < workSessionEventsMaxLimit}
			onLoadMoreEvents={() => setEventsLimit((current) => Math.min(current + workSessionEventsPageSize, workSessionEventsMaxLimit))}
		/>
	);

	const conversationPane = (
		<Stack gap="xs" h="100%" style={{ minHeight: 0 }} data-testid="work-session-conversation-pane">
			<div style={{ flex: 1, minHeight: 0 }}>
				{scope ? <Chat scope={scope} /> : null}
			</div>
			<WorkSessionFollowUpNotice status={status} error={followUpError} />
		</Stack>
	);

	return (
		<FullHeightPage data-testid="work-session-detail-page">
			<Stack gap="sm" h="100%" style={{ minHeight: 0 }}>
				<Group gap="xs" wrap="nowrap">
					{isMobile ? (
						<Tooltip label={t("pages.workSessions.detail.showPlan", "Show plan")}>
							<ActionIcon variant="subtle" onClick={planDrawer.open} aria-label={t("pages.workSessions.detail.showPlan", "Show plan")} data-testid="work-session-plan-toggle">
								<IconLayoutSidebar size={18} />
							</ActionIcon>
						</Tooltip>
					) : null}
					<Text fw={700} lineClamp={1} style={{ flex: 1, minWidth: 0 }} data-testid="work-session-title">
						{sessionQuery.data.title}
					</Text>
					<Badge size="sm" variant="light" color="gray">
						{t(`pages.workSessions.kind.${toWorkSessionKind(sessionQuery.data.kind)}`, sessionQuery.data.kind ?? "")}
					</Badge>
					<Menu position="bottom-end" withinPortal={true}>
						<Menu.Target>
							<ActionIcon variant="subtle" aria-label={t("pages.workSessions.detail.actions", "Session actions")} data-testid="work-session-actions">
								<IconDotsVertical size={18} />
							</ActionIcon>
						</Menu.Target>
						<Menu.Dropdown>
							<Menu.Item leftSection={<IconPencil size={14} />} onClick={editDialog.open} data-testid="work-session-edit">
								{t("pages.workSessions.edit.open", "Edit")}
							</Menu.Item>
							<Menu.Item
								color="red"
								leftSection={<IconTrash size={14} />}
								onClick={() => {
									handleDelete().catch(() => undefined);
								}}
								data-testid="work-session-delete"
							>
								{t("pages.workSessions.delete.open", "Delete")}
							</Menu.Item>
						</Menu.Dropdown>
					</Menu>
					{isMobile ? (
						<Tooltip label={t("pages.workSessions.detail.showDetails", "Show findings and artifacts")}>
							<ActionIcon variant="subtle" onClick={sideDrawer.open} aria-label={t("pages.workSessions.detail.showDetails", "Show findings and artifacts")} data-testid="work-session-side-toggle">
								<IconLayoutSidebarRight size={18} />
							</ActionIcon>
						</Tooltip>
					) : null}
				</Group>

				{deleteError ? (
					<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="work-session-delete-error">
						{deleteError}
					</Alert>
				) : null}

				<EditWorkSessionDialog
					opened={editDialogOpened}
					status={status}
					initialTitle={sessionQuery.data.title ?? ""}
					initialObjective={sessionQuery.data.objective ?? ""}
					isSubmitting={updateSession.isPending}
					errorMessage={
						updateSession.isError
							? apiErrorMessage(updateSession.error, t("pages.workSessions.edit.failed", "Could not save those changes."))
							: undefined
					}
					onClose={() => {
						updateSession.reset();
						editDialog.close();
					}}
					onSubmit={handleSaveEdits}
				/>

				{isMobile ? (
					<>
						<div style={{ flex: 1, minHeight: 0 }}>{conversationPane}</div>
						<Drawer
							opened={planDrawerOpened}
							onClose={planDrawer.close}
							position="left"
							size="85%"
							title={t("pages.workSessions.plan.title", "Plan")}
							// On `content`, not the root: Mantine spreads an unknown prop onto the zero-size portal root,
							// which Playwright reports as hidden. Same rule DialogShell applies.
							attributes={{ content: { "data-testid": "work-session-plan-drawer" } }}
						>
							{planPanel}
						</Drawer>
						<Drawer
							opened={sideDrawerOpened}
							onClose={sideDrawer.close}
							position="right"
							size="85%"
							title={t("pages.workSessions.detail.details", "Details")}
							attributes={{ content: { "data-testid": "work-session-side-drawer" } }}
						>
							{sidePanel}
						</Drawer>
					</>
				) : (
					<div
						data-testid="work-session-detail-grid"
						style={{
							display: "grid",
							gridTemplateColumns: "320px minmax(0, 1fr) minmax(380px, 420px)",
							gridTemplateRows: "minmax(0, 1fr)",
							gap: "var(--mantine-spacing-md)",
							flex: 1,
							minHeight: 0,
						}}
					>
						{planPanel}
						{conversationPane}
						{sidePanel}
					</div>
				)}
			</Stack>
		</FullHeightPage>
	);
}
