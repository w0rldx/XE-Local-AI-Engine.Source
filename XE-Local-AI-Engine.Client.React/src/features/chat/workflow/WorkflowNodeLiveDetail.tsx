import { Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { ThoughtsSection } from "@/features/chat/components/ThoughtsSection";
import type { ChatStreamingState } from "@/features/chat/models/ChatModels";

const modelLoadingPhases: ReadonlySet<string> = new Set(["preparing_runtime", "loading_model"]);

interface WorkflowNodeLiveDetailProps {
	readonly nodeKey: string;
	readonly stream: ChatStreamingState | undefined;
}

/**
 * A running node's live output: its reasoning behind the chat's own collapsed Thoughts disclosure, or — until reasoning
 * arrives — only what the frames said (the runtime phase, the token count). Nothing the stream did not say is shown.
 */
export function WorkflowNodeLiveDetail({ nodeKey, stream }: WorkflowNodeLiveDetailProps) {
	const { t } = useTranslation();
	if (!stream) {
		return null;
	}
	if (stream.reasoning?.trim()) {
		return <ThoughtsSection messageId={`workflow-node-${nodeKey}`} reasoning={stream.reasoning} />;
	}
	const facts = [
		...(stream.isQueued ? [t("pages.chat.workflow.live.queued", "Waiting for the model")] : []),
		...(stream.runtimePhase && modelLoadingPhases.has(stream.runtimePhase)
			? [t("pages.chat.loadingModel", "Loading model…")]
			: []),
		// The fold clears the phase on every content delta, so streamed content is itself the evidence of generation.
		...(stream.runtimePhase === "generating" || stream.content.length > 0
			? [t("pages.chat.workflow.live.generating", "Generating")]
			: []),
		...(stream.outputTokens == null
			? []
			: [t("pages.chat.workflow.live.tokens", "{{count}} tokens", { count: stream.outputTokens })]),
	];
	if (facts.length === 0) {
		return null;
	}
	return (
		<Text size="xs" c="dimmed" data-testid={`chat-workflow-live-${nodeKey}`}>
			{facts.join(" · ")}
		</Text>
	);
}
