import { Badge, Loader, Text } from "@mantine/core";
import { IconClock } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

/* eslint-disable react-doctor/no-many-boolean-props -- These flags are independent wire states (active, queued, delayed, content) rather than one mutually exclusive mode. */

// The runtime phases that precede the first token while a local model cold-loads. Generating and absent
// phases fall through to the normal typing/streaming affordance.
const modelLoadingPhases = new Set(["preparing_runtime", "loading_model"]);

// Errors are NOT rendered here: a failed turn shows its error as a highlighted block inside the assistant
// bubble (see ChatMessage) so it renders exactly once and survives reload. This footer only conveys the
// transient queued/streaming/delayed affordances.
interface StreamingIndicatorProps {
	hasContent?: boolean;
	isDelayed?: boolean;
	isQueued?: boolean;
	isActive: boolean;
	// Pre-first-token runtime phase from the stream; "preparing_runtime"/"loading_model" drive the model-loading
	// affordance. Absent (cloud/Ollama or after generation begins) falls back to the normal indicator.
	runtimePhase?: string | null;
}

export function StreamingIndicator({ hasContent = false, isDelayed = false, isQueued = false, isActive, runtimePhase }: StreamingIndicatorProps) {
	const { t } = useTranslation();

	// Queued is distinct from streaming/typing: the turn is accepted but waiting behind another active
	// invocation, so show a paused clock affordance with no typing text.
	if (isQueued && isActive) {
		return (
			<Badge
				color="gray"
				size="sm"
				variant="light"
				leftSection={<IconClock size={12} />}
				data-testid="chat-stream-queued-indicator"
			>
				{t("pages.chat.queued", "Queued — waiting for current task")}
			</Badge>
		);
	}

	// A cold model load happens before the first token: show a distinct "Loading model…" affordance (with a spinner)
	// so the wait reads as legitimate progress rather than an apparent hang. Only while active and before content.
	if (isActive && !hasContent && runtimePhase != null && modelLoadingPhases.has(runtimePhase)) {
		return (
			<Text
				size="sm"
				c="dimmed"
				component="span"
				style={{ display: "inline-flex", alignItems: "center", gap: 6 }}
				data-testid="chat-stream-loading-model-indicator"
			>
				<Loader size={12} type="dots" />
				{t("pages.chat.loadingModel", "Loading model…")}
			</Text>
		);
	}

	if (!isActive || !hasContent) {
		return null;
	}

	return (
		<Text size="sm" c="dimmed" data-testid={isDelayed ? "chat-stream-delayed-indicator" : "chat-streaming-indicator"}>
			{isDelayed ? t("pages.chat.waitingForResponse", "Waiting for response") : t("pages.chat.streaming", "streaming")}
		</Text>
	);
}
