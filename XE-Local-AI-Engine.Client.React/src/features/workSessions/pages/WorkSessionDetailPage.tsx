import { Anchor, Loader, Stack, VisuallyHidden } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { Link, useNavigate } from "@tanstack/react-router";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { FullHeightPage } from "@/core/ui/components/FullHeightPage/FullHeightPage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { usePaneLayoutMode } from "@/core/ui/components/ResponsivePaneLayout/usePaneLayoutMode";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import type { ChatScope } from "@/features/chat/models/ChatModels";
import { Chat } from "@/features/chat/pages/Chat";
import { EditWorkSessionDialog } from "@/features/workSessions/components/EditWorkSessionDialog";
import { WorkSessionDetailLayout } from "@/features/workSessions/components/WorkSessionDetailLayout";
import { WorkSessionFollowUpNotice } from "@/features/workSessions/components/WorkSessionFollowUpNotice";
import { WorkSessionPlanPanel } from "@/features/workSessions/components/WorkSessionPlanPanel";
import { WorkSessionsDisabledAlert } from "@/features/workSessions/components/WorkSessionsDisabledAlert";
import { WorkSessionSidePanel } from "@/features/workSessions/components/WorkSessionSidePanel";
import { useWorkSessionHub } from "@/features/workSessions/hooks/useWorkSessionHub";
import {
	isObjectiveNotMet,
	isTerminalWorkSessionStatus,
	toWorkSessionKind,
	toWorkSessionStatus,
} from "@/features/workSessions/models/WorkSessionModels";
import {
	useDeleteWorkSession,
	usePostWorkSessionMessage,
	useUpdateWorkSession,
	useWorkSession,
	useWorkSessionArtifacts,
	useWorkSessionCapability,
	useWorkSessionCheckpoints,
	useWorkSessionEvents,
	useWorkSessionFindings,
	useWorkSessionLifecycle,
	useWorkSessionTasks,
	workSessionEventsMaxLimit,
	workSessionEventsPageSize,
} from "@/features/workSessions/queries/useWorkSessions";

export function WorkSessionDetailPage({ sessionId }: { sessionId: string }) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const navigate = useNavigate();
	// One decision for the whole page: the header toggles, the two Drawers and the pane grid all read this. The ref
	// goes on the layout's frame, because that is the element the panes are actually laid out inside.
	const { ref: paneContainerRef, isNarrow: isMobile } = usePaneLayoutMode();
	const [planDrawerOpened, planDrawer] = useDisclosure(false);
	const [sideDrawerOpened, sideDrawer] = useDisclosure(false);
	const [eventsLimit, setEventsLimit] = useState(workSessionEventsPageSize);
	const [followUpError, setFollowUpError] = useState<string | undefined>(undefined);
	const [editDialogOpened, editDialog] = useDisclosure(false);
	const [deleteError, setDeleteError] = useState<string | undefined>(undefined);

	// This route is reachable by bookmark or pasted URL on a node that has the feature switched off, where the whole
	// family answers a bodyless 404 — read as "This work session could not be loaded", which is what a genuinely
	// missing id says too. The capability read is what separates the two, and everything below is gated on it.
	const capabilityQuery = useWorkSessionCapability();
	const workSessionsEnabled = capabilityQuery.data?.enabled === true;
	const sessionQuery = useWorkSession(sessionId, { enabled: workSessionsEnabled });
	const conversationId = sessionQuery.data?.conversationId;
	// Passing no session id keeps the hub idle, which is the seam it already has for "nothing to subscribe to yet".
	const live = useWorkSessionHub(workSessionsEnabled ? sessionId : undefined, conversationId);
	// Start every feed in parallel with the detail request, but once that authoritative request has terminally failed,
	// stop subordinate work even if a failed hub subscription has enabled fallback polling.
	const feedsEnabled = workSessionsEnabled && !sessionQuery.isError;
	const poll = { pollIntervalMs: feedsEnabled ? live.pollIntervalMs : undefined, enabled: feedsEnabled };

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
		.toSorted((left, right) => (right.sequence ?? 0) - (left.sequence ?? 0))
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

	// Every state of this page carries exactly one `<h1>`. Once the session is loaded that is its title, rendered by
	// `WorkSessionDetailLayout`; until then there is no name to show, so the navigation label stands in, visually
	// hidden and first in the container so the full-height flex column is unaffected.
	const fallbackHeading = <VisuallyHidden component="h1">{t("navigation.workSessions", "Work Sessions")}</VisuallyHidden>;

	// The capability answer gates everything below, so it is resolved first. It reuses the page's own loader rather
	// than inventing a second spinner: from the operator's side this is still "the page is coming up".
	if (capabilityQuery.isLoading) {
		return (
			<FullHeightPage ref={paneContainerRef} data-testid="work-session-detail-page">
				{fallbackHeading}
				<Loader data-testid="work-session-detail-loading" />
			</FullHeightPage>
		);
	}

	// Ahead of the pending branch below, which a gated `sessionQuery` would otherwise sit in forever.
	if (capabilityQuery.error || !workSessionsEnabled) {
		return (
			<FullHeightPage ref={paneContainerRef} data-testid="work-session-detail-page">
				{fallbackHeading}
				<WorkSessionsDisabledAlert error={capabilityQuery.error} />
			</FullHeightPage>
		);
	}

	if (sessionQuery.isPending) {
		return (
			<FullHeightPage ref={paneContainerRef} data-testid="work-session-detail-page">
				{fallbackHeading}
				<Loader data-testid="work-session-detail-loading" />
			</FullHeightPage>
		);
	}

	if (sessionQuery.isError || !sessionQuery.data) {
		return (
			<FullHeightPage ref={paneContainerRef} data-testid="work-session-detail-page">
				{fallbackHeading}
				<InlineErrorAlert
					message={apiErrorMessage(
						sessionQuery.error,
						t("pages.workSessions.detail.notFound", "This work session could not be loaded."),
					)}
					variant="light"
					data-testid="work-session-detail-error"
				>
					<Anchor component={Link} to="/work-sessions" size="sm" data-testid="work-session-detail-back">
						{t("pages.workSessions.detail.back", "Back to work sessions")}
					</Anchor>
				</InlineErrorAlert>
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
			objectiveNotMet={isObjectiveNotMet(events)}
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
			onLoadMoreEvents={() =>
				setEventsLimit((current) => Math.min(current + workSessionEventsPageSize, workSessionEventsMaxLimit))
			}
		/>
	);

	const conversationPane = (
		<Stack gap="xs" h="100%" style={{ minHeight: 0 }} data-testid="work-session-conversation-pane">
			<div style={{ flex: 1, minHeight: 0 }}>{scope ? <Chat scope={scope} /> : null}</div>
			<WorkSessionFollowUpNotice status={status} error={followUpError} />
		</Stack>
	);

	const editDialogNode = (
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
	);

	return (
		<WorkSessionDetailLayout
			containerRef={paneContainerRef}
			title={sessionQuery.data.title ?? ""}
			kindLabel={t(`pages.workSessions.kind.${toWorkSessionKind(sessionQuery.data.kind)}`, sessionQuery.data.kind ?? "")}
			isMobile={isMobile}
			deleteError={deleteError}
			planDrawerOpened={planDrawerOpened}
			sideDrawerOpened={sideDrawerOpened}
			onOpenPlan={planDrawer.open}
			onClosePlan={planDrawer.close}
			onOpenSide={sideDrawer.open}
			onCloseSide={sideDrawer.close}
			onEdit={editDialog.open}
			onDelete={() => {
				handleDelete().catch(() => undefined);
			}}
			planPanel={planPanel}
			sidePanel={sidePanel}
			conversationPane={conversationPane}
			editDialog={editDialogNode}
		/>
	);
}
