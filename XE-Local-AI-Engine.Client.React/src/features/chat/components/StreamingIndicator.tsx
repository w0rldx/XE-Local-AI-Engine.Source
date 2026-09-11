import { Badge, Loader, Text } from "@mantine/core";
import { IconClock } from "@tabler/icons-react";
import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { formatDurationCompact } from "@/core/formatting/TimeFormatting";

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
	// Server-stamped ISO-8601 UTC time of that phase transition. Anchors the cold-load elapsed timer so a
	// reload mid-load resumes the count; absent (older node, cloud/Ollama) falls back to first-observed time.
	runtimePhaseChangedAtUtc?: string | null;
}

export function StreamingIndicator({
	hasContent = false,
	isDelayed = false,
	isQueued = false,
	isActive,
	runtimePhase,
	runtimePhaseChangedAtUtc,
}: StreamingIndicatorProps) {
	const { t } = useTranslation();
	const [elapsedMs, setElapsedMs] = useState(0);
	// First render at which this component saw the loading phase, used only when the server sent no timestamp.
	const observedAtRef = useRef<number | null>(null);

	// Hoisted above the early returns so the ticker's hooks stay unconditional. `!isQueued` matters: the queued
	// branch returns first, so a queued turn carrying a loading phase would otherwise arm a 1 Hz interval and
	// re-render every second while rendering only the queued badge.
	const isModelLoading = isActive && !isQueued && !hasContent && runtimePhase != null && modelLoadingPhases.has(runtimePhase);

	useEffect(() => {
		if (!isModelLoading) {
			observedAtRef.current = null;
			return undefined;
		}

		// The server timestamp is authoritative: it survives a page reload mid-load, which is the whole point of
		// the counter. First-observed time is the fallback for a node that sends no timestamp.
		const serverAnchor = runtimePhaseChangedAtUtc == null ? Number.NaN : Date.parse(runtimePhaseChangedAtUtc);
		if (Number.isNaN(serverAnchor) && observedAtRef.current === null) {
			observedAtRef.current = Date.now();
		}
		const anchor = Number.isNaN(serverAnchor) ? (observedAtRef.current ?? Date.now()) : serverAnchor;

		const tick = () => setElapsedMs(Date.now() - anchor);
		tick();
		const id = window.setInterval(tick, 1000);

		return () => window.clearInterval(id);
	}, [isModelLoading, runtimePhaseChangedAtUtc]);

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
	if (isModelLoading) {
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
				<span data-testid="chat-stream-loading-model-elapsed">
					{t("pages.chat.loadingModelElapsed", "· {{elapsed}}", { elapsed: formatDurationCompact(elapsedMs) })}
				</span>
			</Text>
		);
	}

	if (!isActive || !hasContent) {
		return null;
	}

	return (
		<Text size="sm" c="dimmed" data-testid={isDelayed ? "chat-stream-delayed-indicator" : "chat-streaming-indicator"}>
			{isDelayed ? t("pages.chat.waitingForResponse", "Waiting for response") : t("pages.chat.streaming", "Receiving response")}
		</Text>
	);
}
