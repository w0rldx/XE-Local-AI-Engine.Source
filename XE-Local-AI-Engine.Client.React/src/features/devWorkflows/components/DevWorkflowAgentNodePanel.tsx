import { Alert, Button, Text } from "@mantine/core";
import { IconExternalLink } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import type { ChatScope } from "@/features/chat/models/ChatModels";
import { Chat } from "@/features/chat/pages/Chat";
import type { DevWorkflowNodeRunDetailResponse } from "@/features/devWorkflows/models/DevWorkflowModels";
import { useWorkSessionHub } from "@/features/workSessions/hooks/useWorkSessionHub";

/** Tall enough for a few turns without swallowing the panel; the transcript scrolls inside its own frame. */
const EMBEDDED_CHAT_HEIGHT = 420;

export interface DevWorkflowAgentNodePanelProps {
	readonly nodeRun: DevWorkflowNodeRunDetailResponse;
}

/**
 * An Agent node's bound agent, its transcript, and the way out to the full session view (P4 §2.6).
 *
 * The transcript is the chat page itself under a `ChatScope` — the same mechanism `WorkSessionDetailPage` uses, and
 * the pre-approved `devWorkflows → chat` edge (§2.10). Everything the chat page does (the streaming fold, tool-call
 * cards, the cold-load re-attach) comes with it; the scope only pins the view and takes the composer away. The
 * composer is disabled rather than redirected: N2 makes the RUNTIME the single writer of invocations on a workflow
 * node's conversation, and a follow-up typed here would be a second writer.
 *
 * `resumeNonce` comes from `useWorkSessionHub`, which is where that number is computed and nowhere else (R-C6 allows
 * the cross-feature edge). It is bumped on the session's `step` ping, which is published at step START while the
 * invocation is still resumable — that is what makes a live node stream here rather than back-fill a beat later.
 *
 * The purged branch is kept exactly as it was: when `workSessionAvailable` is false, nothing mounts against the dead
 * conversation id. An empty chat pane is indistinguishable from a session that simply has not spoken yet, and the
 * node's own events and artifacts are workflow-owned and still present — so the UI says WHICH thing is missing.
 */
export function DevWorkflowAgentNodePanel({ nodeRun }: DevWorkflowAgentNodePanelProps) {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const workSessionId = nodeRun.workSessionId ?? undefined;
	const conversationId = nodeRun.conversationId ?? undefined;
	const isPurged = nodeRun.workSessionAvailable === false;
	// Not subscribed at all for a purged session: there is nothing left to be notified about, and the hook would open a
	// subscription on a session id the server no longer has.
	const live = useWorkSessionHub(isPurged ? undefined : workSessionId, isPurged ? undefined : conversationId);

	const scope = useMemo<ChatScope | undefined>(
		() =>
			conversationId && !isPurged
				? {
						conversationId,
						pinnedAgentId: nodeRun.agentDefinitionId ?? undefined,
						resumeNonce: live.resumeNonce,
						composerDisabled: true,
						embedded: true,
					}
				: undefined,
		[conversationId, isPurged, nodeRun.agentDefinitionId, live.resumeNonce],
	);

	return (
		<SectionCard title={t("pages.devWorkflows.node.agent", "Agent")} gap="xs" data-testid="dev-workflow-node-agent">
			<Text size="sm">
				{nodeRun.agentDisplayName ?? t("pages.devWorkflows.node.agentUnbound", "No agent is bound to this node.")}
			</Text>
			{nodeRun.modelLabel ? (
				<Text size="xs" c="dimmed">
					{nodeRun.modelLabel}
				</Text>
			) : null}

			{workSessionId && isPurged ? (
				// The node-run row outlives its work session on purpose (the reference is loose). Saying WHICH thing is
				// missing matters: the node's own events and artifacts are workflow-owned and still here.
				<Alert color="gray" variant="light" data-testid="dev-workflow-node-session-purged">
					{t(
						"pages.devWorkflows.node.sessionPurged",
						"The agent's transcript is no longer available. This node's events and artifacts are unaffected.",
					)}
				</Alert>
			) : null}

			{scope ? (
				<div style={{ height: EMBEDDED_CHAT_HEIGHT, minHeight: 0 }} data-testid="dev-workflow-node-transcript">
					<Chat scope={scope} />
				</div>
			) : null}

			{workSessionId && !isPurged && nodeCapabilities.workSessions ? (
				// Kept beside the embedded transcript, not replaced by it: the session route carries the plan, findings and
				// checkpoints this pane has no room for, and re-hosting those would fork the one place each is rendered.
				<Button
					size="xs"
					variant="subtle"
					leftSection={<IconExternalLink size={14} />}
					onClick={() => navigate({ to: "/work-sessions/$sessionId", params: { sessionId: workSessionId } })}
					data-testid="dev-workflow-node-session-link"
				>
					{t("pages.devWorkflows.node.openSession", "Open the agent's work session")}
				</Button>
			) : null}
		</SectionCard>
	);
}
