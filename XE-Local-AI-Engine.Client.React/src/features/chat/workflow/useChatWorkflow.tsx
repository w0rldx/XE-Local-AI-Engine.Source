// Chat workflow mode: a conversation whose messages go to a Chat graph workflow instead of the model stream.
//
// Server state is the Graph Workflows queries (definitions, the conversation's bound runs, run detail, node documents)
// kept fresh by the existing run hub; the only UI state is the store's picker/dismiss/toggle. The send goes through the
// graph-workflows messages endpoint — NOT a synthetic ChatScope: `onSendOverride` carries no attachments and a scope
// turns every selection write into a no-op.

import { Alert, Button, Group, Switch, Text } from "@mantine/core";
import { useQueryClient } from "@tanstack/react-query";
import { type ReactNode, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { titleFromContent } from "@/features/chat/models/ChatConversationDerivations";
import { errorMessage } from "@/features/chat/models/ChatErrorMessage";
import type { ChatConversationModel, ChatWorkflowSlots } from "@/features/chat/models/ChatModels";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";
import {
	activeWorkflowNode,
	graphAcceptsAttachments,
	isBusyWorkflowRun,
	isLiveWorkflowRun,
	liveWorkflowNode,
	steerableWorkflowNode,
	toWorkflowPath,
	workflowNodeFailureReason,
	workflowNodeLabel,
	workflowNodeWithStatus,
} from "@/features/chat/workflow/ChatWorkflowModels";
import { useChatWorkflowStore } from "@/features/chat/workflow/ChatWorkflowStore";
import { useGraphWorkflowNodeActivity } from "@/features/chat/workflow/useGraphWorkflowNodeActivity";
import { WorkflowActivityBlock } from "@/features/chat/workflow/WorkflowActivityBlock";
import { WorkflowNodeLiveDetail } from "@/features/chat/workflow/WorkflowNodeLiveDetail";
import { WorkflowRunStatusCard } from "@/features/chat/workflow/WorkflowRunStatusCard";
import { WorkflowSelectorCard } from "@/features/chat/workflow/WorkflowSelectorCard";
import { graphWorkflowConflictTypes, readGraphWorkflowConflict } from "@/features/graphWorkflows/api/GraphWorkflowConflict";
import { useGraphWorkflowRunHub } from "@/features/graphWorkflows/hooks/useGraphWorkflowRunHub";
import {
	type GraphWorkflowConversationRunResponse,
	narrowGraphWorkflowRunStatus,
	type SendGraphWorkflowChatMessageRequest,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import {
	GRAPH_WORKFLOW_CONVERSATION_RUN_PAGE_SIZE,
	graphWorkflowInvalidationKey,
	graphWorkflowQueryIds,
	useCancelGraphWorkflowRun,
	useGraphWorkflowConversationRuns,
	useGraphWorkflowDefinition,
	useGraphWorkflowDefinitions,
	useGraphWorkflowNodeRun,
	useGraphWorkflowRun,
	useSendGraphWorkflowChatMessage,
	useSteerGraphWorkflowNodeRun,
} from "@/features/graphWorkflows/queries/useGraphWorkflows";

type SendNotice =
	| { readonly kind: "busy" }
	| { readonly kind: "selectionChanged" }
	| { readonly kind: "attachments" }
	| { readonly kind: "error"; readonly message: string };

interface UseChatWorkflowOptions {
	/** Graph Workflows capability on and the chat not owner-scoped. Off ⇒ no queries, no slots. */
	readonly enabled: boolean;
	readonly conversationId: string;
	/** The normal send's create-or-load: titles a new conversation from the first message. */
	readonly resolveSendConversation: (content: string) => Promise<ChatConversationModel>;
	readonly cacheConversation: (conversation: ChatConversationModel) => void;
	readonly markConversationTitled: (conversationId: string) => void;
	/** The composer's attachment chips. Sent only when the selected workflow accepts them. */
	readonly attachmentFileIds: readonly string[];
	/** A normal chat reply is streaming: the picker is locked while it runs. */
	readonly isSending: boolean;
}

export interface ChatWorkflowMode {
	/** A workflow is selected: the composer's send must go through {@link ChatWorkflowMode.send}. */
	readonly active: boolean;
	/** Resolves when the server accepted the message; rejects (keeping the draft) when it refused it. */
	readonly send: (content: string) => Promise<void>;
	/**
	 * No explicit pick and the bound-run list has not loaded (a reload, a fast conversation switch): whether this
	 * conversation is in workflow mode is not known yet, so the send must go through
	 * {@link ChatWorkflowMode.sendWhenResolved}.
	 */
	readonly bindingUnresolved: boolean;
	/**
	 * Waits for the bound-run list, then sends to the newest run's Chat workflow or, when there is none, calls
	 * `normalSend`. Rejects (keeping the draft) when the list cannot be read; the runs-failed notice offers Retry.
	 */
	readonly sendWhenResolved: (content: string, normalSend: () => void) => Promise<void>;
	readonly slots: ChatWorkflowSlots | undefined;
}

const EMPTY_RUNS: readonly GraphWorkflowConversationRunResponse[] = [];

export function useChatWorkflow({
	enabled,
	conversationId,
	resolveSendConversation,
	cacheConversation,
	markConversationTitled,
	attachmentFileIds,
	isSending,
}: UseChatWorkflowOptions): ChatWorkflowMode {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const queryClient = useQueryClient();
	const explicitSelection = useChatWorkflowStore((state) => state.selectedDefinitionByConversation[conversationId]);
	const dismissedRunId = useChatWorkflowStore((state) => state.dismissedRunByConversation[conversationId]);
	const showActivity = useChatWorkflowStore((state) => state.showActivity);
	const { selectDefinition, carryOverPendingPick, dismissRun, setShowActivity } = useChatWorkflowStore((state) => state.actions);
	const [notice, setNotice] = useState<SendNotice | undefined>();
	// One send at a time: the ref refuses re-entry synchronously (a second Enter before the first render), the state
	// drives the disabled Send button. The textarea stays editable either way.
	const inFlight = useRef(false);
	const [sending, setSending] = useState(false);
	// The hub's fallback cadence, fed back into the queries that must keep moving while it is down.
	const [pollIntervalMs, setPollIntervalMs] = useState<number | undefined>();

	// A conversation can appear BEFORE the first send (an upload's ensureConversationId, "New plain chat"): the pick made
	// on the empty composer follows it, or that first send would slip into the normal stream with the workflow still shown.
	const previousConversationId = useRef(conversationId);
	useEffect(() => {
		if (previousConversationId.current.length === 0 && conversationId.length > 0) {
			carryOverPendingPick(conversationId);
		}
		previousConversationId.current = conversationId;
	}, [carryOverPendingPick, conversationId]);

	const definitionsQuery = useGraphWorkflowDefinitions({ enabled });
	const chatDefinitions = useMemo(
		() => (definitionsQuery.data?.definitions ?? []).filter((definition) => definition.kind === "Chat"),
		[definitionsQuery.data],
	);
	const runsQuery = useGraphWorkflowConversationRuns(conversationId || undefined, { enabled, pollIntervalMs });
	const runs = runsQuery.data ?? EMPTY_RUNS;
	const newest = runs[0];

	// An explicit pick wins; otherwise a conversation that has bound runs reopens in the newest run's workflow.
	const selectedDefinitionId = explicitSelection ?? newest?.definitionId ?? "";
	// Workflow mode needs a workflow that still exists: a run bound to a deleted definition keeps its status card and
	// activity, but the composer goes back to normal chat. Until the list has loaded the pick is trusted, so a quick
	// send after a reload cannot slip into the normal stream.
	const selectedExists =
		definitionsQuery.data === undefined || chatDefinitions.some((definition) => definition.id === selectedDefinitionId);
	const active = enabled && selectedDefinitionId !== "" && selectedExists;
	const definitionQuery = useGraphWorkflowDefinition(active ? selectedDefinitionId : undefined);
	const acceptsAttachments = graphAcceptsAttachments(definitionQuery.data?.graph);

	const liveRun = newest && isLiveWorkflowRun(newest.run.status) ? newest : undefined;
	const liveRunId = enabled ? liveRun?.run.id : undefined;
	const live = useGraphWorkflowRunHub(liveRunId);
	useEffect(() => setPollIntervalMs(live.pollIntervalMs), [live.pollIntervalMs]);

	const rereadConversation = useCallback((): void => {
		if (conversationId.length === 0) {
			return;
		}
		queryClient
			.invalidateQueries({ queryKey: nodeChatQueryKeys.conversation(conversationId), exact: true })
			.catch(() => undefined);
		queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversationLists() }).catch(() => undefined);
	}, [conversationId, queryClient]);

	// A lifecycle move (snapshot, `run` or `gate` ping) can change the bound-run list (status, a parked input) and ends
	// the run; a `node` ping cannot, so the list re-reads on `lifecycleSeq` only.
	useEffect(() => {
		if (live.lifecycleSeq === 0 || conversationId.length === 0) {
			return;
		}
		queryClient
			.invalidateQueries({
				queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.conversationRuns, { conversationId }),
			})
			.catch(() => undefined);
	}, [conversationId, live.lifecycleSeq, queryClient]);

	// A publish inserts an assistant message mid-run and rides a `node` ping, so the conversation re-reads on EVERY
	// admitted ping (`watermark`). Counting `node.published` events instead would stall once the run's trail outgrew the
	// event feed's first page, which nothing here pages.
	useEffect(() => {
		if (live.watermark > 0) {
			rereadConversation();
		}
	}, [live.watermark, rereadConversation]);

	const cardRun = newest && (liveRun === newest || dismissedRunId !== newest.run.id) ? newest : undefined;
	const cardRunId = enabled ? cardRun?.run.id : undefined;
	const runDetailQuery = useGraphWorkflowRun(cardRunId, { pollIntervalMs: liveRun ? pollIntervalMs : undefined });
	const path = useMemo(
		() => toWorkflowPath(runDetailQuery.data?.graph, runDetailQuery.data?.nodeRuns ?? []),
		[runDetailQuery.data],
	);
	// The run detail is re-read on EVERY hub ping, the bound-run list only on lifecycle moves, so the detail can be the
	// first to say the run settled (a cancel drains through Cancelling). A terminal status wins from either source, so
	// the card never keeps offering Stop instead of Dismiss on a run that has already ended.
	const detailStatus = runDetailQuery.data?.run.id === cardRun?.run.id ? runDetailQuery.data?.run.status : undefined;
	const cardStatus =
		detailStatus && !isLiveWorkflowRun(detailStatus) ? detailStatus : (cardRun?.run.status ?? detailStatus ?? "");
	const failedNode = narrowGraphWorkflowRunStatus(cardStatus) === "Failed" ? workflowNodeWithStatus(path, "Failed") : undefined;
	const failedNodeQuery = useGraphWorkflowNodeRun(cardRunId, failedNode?.key);
	// The live run's running Agent / LLM Call. The run detail re-reads on every `node` ping, so a new invocation (a retry,
	// a steer) arrives as a new id here and the activity stream re-keys onto it.
	const liveNode = liveRun && liveRun === cardRun ? liveWorkflowNode(path) : undefined;
	const liveActivity = useGraphWorkflowNodeActivity(cardRunId, liveNode?.key, liveNode?.invocationId);
	const liveDetail = liveNode ? <WorkflowNodeLiveDetail nodeKey={liveNode.key} stream={liveActivity.stream} /> : undefined;
	// The model the card's active node really runs on, as its live stream resolved it (a config with no `model` runs on
	// the default). The node document is no fallback: `output.usage` lands only with the settled turn.
	const activeNode = isLiveWorkflowRun(cardStatus) ? activeWorkflowNode(path) : undefined;
	const activeModel = activeNode && activeNode.key === liveNode?.key ? liveActivity.model : undefined;
	const cancelMutation = useCancelGraphWorkflowRun();
	const steerMutation = useSteerGraphWorkflowNodeRun();
	// Intervene is offered on the live run's running/queued Agent or LLM Call, never while a cancel drains.
	const steerable =
		liveRun && liveRun === cardRun && narrowGraphWorkflowRunStatus(cardStatus) !== "Cancelling"
			? steerableWorkflowNode(path)
			: undefined;
	const sendMutation = useSendGraphWorkflowChatMessage();

	const pendingInput = liveRun?.pendingInput ?? undefined;
	const busy =
		liveRun !== undefined && isBusyWorkflowRun(narrowGraphWorkflowRunStatus(liveRun.run.status), pendingInput !== undefined);
	// With no explicit pick the binding IS the bound-run list: until it has loaded (or while it failed), `active` would read
	// false and a send would slip into the normal model stream past a live run. The composer stays usable (a fast
	// switch-then-Enter must not be dropped); the send waits for the list instead — see `sendWhenResolved`.
	const bindingUnresolved =
		enabled && conversationId.length > 0 && explicitSelection === undefined && runsQuery.data === undefined;
	const composerDisabled = active && busy;
	const sendAttachments = acceptsAttachments && pendingInput === undefined;
	// An answer goes to the PARKED run, whatever the picker says: the server refuses a mismatched definition.
	const sendDefinitionId = pendingInput && newest ? newest.definitionId : selectedDefinitionId;

	const refreshConversation = useCallback(
		async (targetConversationId: string): Promise<void> => {
			await Promise.all([
				queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversation(targetConversationId), exact: true }),
				queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversationLists() }),
				queryClient.invalidateQueries({
					queryKey: graphWorkflowInvalidationKey(graphWorkflowQueryIds.conversationRuns, {
						conversationId: targetConversationId,
					}),
				}),
			]);
		},
		[queryClient],
	);

	const sendOnce = async (content: string, definitionId: string, withAttachments: boolean): Promise<void> => {
		setNotice(undefined);
		let conversation: ChatConversationModel;
		try {
			conversation = await resolveSendConversation(content);
		} catch (error) {
			setNotice({ kind: "error", message: errorMessage(error) });
			throw error;
		}
		if (conversationId.length === 0) {
			// The send created the conversation: the pick made before it existed moves onto it.
			carryOverPendingPick(conversation.id);
		} else if (conversation.id !== conversationId) {
			selectDefinition(conversation.id, definitionId);
		}
		// The server does not title a conversation; promote a placeholder title exactly as a normal first send does.
		const title = conversation.title.trim();
		const placeholderTitle = title.length === 0 || title === "New conversation";
		if (
			conversation.origin !== "remote" &&
			placeholderTitle &&
			!conversation.messages.some((message) => message.role === "user")
		) {
			markConversationTitled(conversation.id);
			try {
				cacheConversation(await nodeChatAdapter.renameConversation(conversation.id, titleFromContent(content)));
			} catch {
				// Best effort, as on the normal path: the send proceeds with the placeholder title.
			}
		}

		const body: SendGraphWorkflowChatMessageRequest = {
			requestId: crypto.randomUUID(),
			definitionId,
			content,
			attachmentFileIds: withAttachments && attachmentFileIds.length > 0 ? [...attachmentFileIds] : null,
		};
		const post = (request: SendGraphWorkflowChatMessageRequest) =>
			sendMutation.mutateAsync({ path: { conversationId: conversation.id }, body: request });
		try {
			try {
				await post(body);
			} catch (error) {
				if (readGraphWorkflowConflict(error)?.conflictType !== graphWorkflowConflictTypes.rerunConfirmationRequired) {
					throw error;
				}
				const name = chatDefinitions.find((definition) => definition.id === definitionId)?.name ?? "";
				const confirmed = await confirm({
					title: t("pages.chat.workflow.rerun.title", "Run the workflow again?"),
					description: t(
						"pages.chat.workflow.rerun.description",
						"This workflow has completed. Sending this message will start '{{name}}' again.",
						{ name },
					),
					confirmationText: t("pages.chat.workflow.rerun.confirm", "Start again"),
					cancellationText: t("common.cancel", "Cancel"),
				});
				if (!confirmed) {
					throw error;
				}
				// Nothing was persisted by the refused send, so the same request id is safe to reuse.
				await post({ ...body, confirmRerun: true });
			}
		} catch (error) {
			const conflictType = readGraphWorkflowConflict(error)?.conflictType;
			if (conflictType === graphWorkflowConflictTypes.runBusy) {
				setNotice({ kind: "busy" });
			} else if (conflictType === graphWorkflowConflictTypes.runConflict) {
				// An answer whose definitionId is not the parked run's (the picker moved while a question waited).
				setNotice({ kind: "selectionChanged" });
			} else if (conflictType === graphWorkflowConflictTypes.attachmentsNotAccepted) {
				setNotice({ kind: "attachments" });
			} else if (conflictType !== graphWorkflowConflictTypes.rerunConfirmationRequired) {
				setNotice({ kind: "error", message: errorMessage(error) });
			}
			throw error;
		}
		await refreshConversation(conversation.id);
	};

	const oneAtATime = async (work: () => Promise<void>): Promise<void> => {
		if (inFlight.current) {
			// Rejecting keeps the draft; the first send is still on its way.
			throw new Error("A workflow message is already being sent.");
		}
		inFlight.current = true;
		setSending(true);
		try {
			await work();
		} finally {
			inFlight.current = false;
			setSending(false);
		}
	};

	const send = (content: string): Promise<void> => oneAtATime(() => sendOnce(content, sendDefinitionId, sendAttachments));

	// The definition and attachment rule come from the list just read, never from this render's (unresolved) closure.
	// A parked run is answered with its own definition and no attachments, as `send` does; a fresh run's attachments go
	// along and the server's attachments-not-accepted refusal keeps the draft, since the definition's graph is not read.
	const sendWhenResolved = (content: string, normalSend: () => void): Promise<void> =>
		oneAtATime(async () => {
			// Joins the read already in flight (the reload's or the switch's) rather than cancelling it for a duplicate.
			const result = await runsQuery.refetch({ cancelRefetch: false });
			if (result.data === undefined) {
				throw result.error ?? new Error("The conversation's workflow runs could not be read.");
			}
			const bound = result.data[0];
			const definitions = definitionsQuery.data?.definitions;
			const boundIsChat =
				bound !== undefined &&
				(definitions === undefined ||
					definitions.some((definition) => definition.kind === "Chat" && definition.id === bound.definitionId));
			if (!(bound && boundIsChat)) {
				normalSend();
				return;
			}
			const parked = isLiveWorkflowRun(bound.run.status) && bound.pendingInput != null;
			await sendOnce(content, bound.definitionId, !parked);
		});

	const stop = useCallback(() => {
		if (!liveRun) {
			return;
		}
		cancelMutation
			.mutateAsync({ path: { runId: liveRun.run.id } })
			.then(() => refreshConversation(conversationId))
			.catch((error: unknown) => setNotice({ kind: "error", message: errorMessage(error) }));
	}, [cancelMutation, conversationId, liveRun, refreshConversation]);

	// One operation id per submit: a resend of the same text is a second steer, which the cap bounds. The run is the one
	// the dialog captured, never "whatever is live now": a run that ended mid-draft earns the server's 409, so the draft
	// stays and the operator is told why, instead of the dialog closing as if the steer had landed.
	const steer = useCallback(
		async (runId: string, nodeKey: string, message: string): Promise<void> => {
			try {
				await steerMutation.mutateAsync({
					path: { runId, nodeKey },
					body: { operationId: crypto.randomUUID(), message },
				});
			} catch (error) {
				// A live card can only earn two 409s: the cap, or the node settling before the steer landed.
				const conflictType = readGraphWorkflowConflict(error)?.conflictType;
				throw new Error(
					conflictType === graphWorkflowConflictTypes.steerLimitReached
						? t("pages.chat.workflow.steer.capReached", "This node has been steered the maximum number of times.")
						: conflictType
							? t("pages.chat.workflow.steer.finished", "The node finished before your steering arrived.")
							: errorMessage(error),
				);
			}
		},
		[steerMutation, t],
	);

	const runsByTrigger = useMemo(() => {
		const map = new Map<string, GraphWorkflowConversationRunResponse[]>();
		for (const run of runs.toReversed()) {
			if (run.triggerMessageId) {
				map.set(run.triggerMessageId, [...(map.get(run.triggerMessageId) ?? []), run]);
			}
		}
		return map;
	}, [runs]);

	const unnamed = t("pages.chat.workflow.deletedWorkflow", "Deleted workflow");
	const renderAfterMessage = useCallback(
		(messageId: string): ReactNode => {
			const triggered = runsByTrigger.get(messageId);
			if (!showActivity || !triggered) {
				return null;
			}
			return triggered.map((run) => (
				<WorkflowActivityBlock
					key={run.run.id}
					runId={run.run.id}
					workflowName={run.definitionName ?? unnamed}
					pollIntervalMs={run === liveRun ? pollIntervalMs : undefined}
					{...(run === liveRun && liveNode ? { liveNodeKey: liveNode.key, liveDetail } : {})}
				/>
			));
		},
		[liveDetail, liveNode, liveRun, pollIntervalMs, runsByTrigger, showActivity, unnamed],
	);

	if (!enabled) {
		return { active: false, send, bindingUnresolved: false, sendWhenResolved, slots: undefined };
	}

	const noticeText =
		notice?.kind === "busy"
			? t("pages.chat.workflow.notice.busy", "The workflow is still running. Stop it or wait for it to finish.")
			: notice?.kind === "selectionChanged"
				? t(
						"pages.chat.workflow.notice.selectionChanged",
						"A different workflow is waiting for your answer. Select it again to answer, or Stop it first.",
					)
				: notice?.kind === "attachments"
					? t("pages.chat.workflow.notice.attachments", "This workflow does not accept attachments. Remove them and send again.")
					: notice?.message;

	const composerHeader = (
		<>
			{noticeText ? (
				<Alert
					color="orange"
					variant="light"
					withCloseButton={true}
					onClose={() => setNotice(undefined)}
					data-testid="chat-workflow-notice"
				>
					{noticeText}
				</Alert>
			) : null}
			{pendingInput ? (
				<Alert color="blue" variant="light" data-testid="chat-workflow-input-banner">
					{t("pages.chat.workflow.inputBanner", "Workflow needs your input — {{node}}: {{prompt}}", {
						node: workflowNodeLabel(runDetailQuery.data?.graph, pendingInput.nodeKey),
						prompt: pendingInput.prompt,
					})}
				</Alert>
			) : null}
			{bindingUnresolved && runsQuery.isError ? (
				<Alert color="orange" variant="light" data-testid="chat-workflow-runs-error">
					<Group justify="space-between" wrap="nowrap">
						<Text size="sm">
							{t(
								"pages.chat.workflow.notice.runsFailed",
								"Could not load this conversation's workflow runs, so sending is paused. Retry to continue.",
							)}
						</Text>
						<Button size="xs" variant="light" onClick={() => runsQuery.refetch().catch(() => undefined)}>
							{t("common.retry", "Retry")}
						</Button>
					</Group>
				</Alert>
			) : null}
			{active && busy ? (
				<Text size="xs" c="dimmed" data-testid="chat-workflow-locked-hint">
					{steerable
						? t("pages.chat.workflow.lockedHintSteer", "Workflow running — Intervene or Stop.")
						: t("pages.chat.workflow.lockedHint", "Workflow running — Stop it or wait.")}
				</Text>
			) : null}
			{cardRun && runDetailQuery.data ? (
				<WorkflowRunStatusCard
					runId={cardRun.run.id}
					definitionId={cardRun.definitionId}
					workflowName={cardRun.definitionName ?? unnamed}
					status={cardStatus}
					path={path}
					failureReason={workflowNodeFailureReason(failedNodeQuery.data?.status, failedNodeQuery.data?.error)}
					stopping={cancelMutation.isPending}
					onStop={stop}
					onDismiss={() => dismissRun(conversationId, cardRun.run.id)}
					liveDetail={liveDetail}
					activeModel={activeModel}
					onSteer={steer}
				/>
			) : null}
			{runs.length >= GRAPH_WORKFLOW_CONVERSATION_RUN_PAGE_SIZE ? (
				<Text size="xs" c="dimmed" data-testid="chat-workflow-runs-capped">
					{t(
						"pages.chat.workflow.runsCapped",
						"Showing activity for the {{count}} most recent workflow runs in this conversation.",
						{ count: GRAPH_WORKFLOW_CONVERSATION_RUN_PAGE_SIZE },
					)}
				</Text>
			) : null}
			{runs.length > 0 ? (
				<Group justify="flex-end">
					<Switch
						size="xs"
						labelPosition="left"
						label={t("pages.chat.workflow.showActivity", "Show workflow activity")}
						checked={showActivity}
						onChange={(event) => setShowActivity(event.currentTarget.checked)}
						data-testid="chat-workflow-activity-toggle"
					/>
				</Group>
			) : null}
		</>
	);

	const selectedName = chatDefinitions.find((definition) => definition.id === selectedDefinitionId)?.name;
	return {
		active,
		send,
		bindingUnresolved,
		sendWhenResolved,
		slots: {
			selector: (
				<WorkflowSelectorCard
					options={chatDefinitions}
					selectedDefinitionId={selectedDefinitionId}
					// Locked while any bound run is live (working or parked): an answer belongs to the parked run, and a
					// run that is working takes no message — "No workflow" included, so normal chat cannot race it.
					disabled={isSending || sending || liveRun !== undefined}
					onSelect={(definitionId) => selectDefinition(conversationId, definitionId)}
				/>
			),
			composerHeader,
			composerDisabled,
			sendDisabled: sending,
			composerPlaceholder: pendingInput
				? t("pages.chat.workflow.answerPlaceholder", "Answer the workflow's question")
				: active
					? t("pages.chat.workflow.placeholder", "Message {{name}}", { name: selectedName ?? unnamed })
					: undefined,
			attachmentsDisabledHint:
				active && !sendAttachments
					? pendingInput
						? t("pages.chat.workflow.attachmentsNotWithAnswer", "Attachments are not sent with an answer.")
						: t("pages.chat.workflow.attachmentsNotAccepted", "This workflow does not accept attachments.")
					: undefined,
			renderAfterMessage,
		},
	};
}
