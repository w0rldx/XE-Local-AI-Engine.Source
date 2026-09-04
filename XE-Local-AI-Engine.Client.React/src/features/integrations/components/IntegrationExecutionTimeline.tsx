import { Badge, Group, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { formatIntegrationTimestamp } from "@/features/integrations/components/IntegrationFormatters";
import type { IntegrationExecutionEvent } from "@/features/integrations/models/IntegrationModels";

interface IntegrationExecutionTimelineProps {
	events: readonly IntegrationExecutionEvent[];
	isLoading: boolean;
}

// The payloads the nine persisted event types carry, per the backend's own writers. Everything is optional because a
// detail line must degrade to nothing rather than throw on a shape this client has not seen.
interface EventDetail {
	readonly name?: string;
	readonly ok?: boolean;
	readonly category?: string | null;
	readonly summary?: string | null;
	readonly tokens?: number;
	readonly durationMs?: number;
	readonly contentType?: string;
	readonly payload?: unknown;
}

/**
 * `detailJson` arrives as already-decrypted text. It is server-written JSON, but parsing is still guarded: a row that
 * cannot be parsed is shown with no detail line rather than taking the whole timeline down with it.
 */
function parseDetail(detailJson: string | null): EventDetail | null {
	if (detailJson === null || detailJson.trim() === "") {
		return null;
	}
	try {
		const parsed: unknown = JSON.parse(detailJson);
		return typeof parsed === "object" && parsed !== null ? (parsed as EventDetail) : null;
	} catch {
		return null;
	}
}

/** Pretty-prints the caller's payload for the read-only viewer, falling back to the raw text when it will not parse. */
function formatOutputPayload(detail: EventDetail | null, detailJson: string | null): string {
	if (detail === null) {
		return detailJson ?? "";
	}
	return JSON.stringify(detail.payload ?? detail, null, 2);
}

function detailLine(eventType: string, detail: EventDetail | null): string | null {
	if (detail === null) {
		return null;
	}

	switch (eventType) {
		case "tool.started":
			return detail.name ?? null;
		case "tool.completed":
			return detail.name === undefined ? null : `${detail.name} — ${detail.ok === false ? "error" : "ok"}`;
		case "execution.completed": {
			const duration = detail.durationMs === undefined ? null : `${(detail.durationMs / 1000).toFixed(1)}s`;
			// `tokens` is omitted entirely when the provider reported no usage, so it is never rendered as a null.
			const tokens = detail.tokens === undefined ? null : `${detail.tokens} tokens`;
			return [duration, tokens].filter((part) => part !== null).join(" · ") || null;
		}
		case "execution.failed":
			// Both parts render verbatim. The category is a closed backend set and the summary is content-free by
			// construction, so neither is translated and neither needs redacting.
			return [detail.category ?? null, detail.summary ?? null].filter((part) => part !== null).join(" — ") || null;
		default:
			return null;
	}
}

/**
 * The persisted event list of one execution. Sequence values ascend but may SKIP — a failed durable write leaves a
 * permanent hole — so a gap is rendered as the two rows it is, never as a missing row or a loading state.
 */
export function IntegrationExecutionTimeline({ events, isLoading }: IntegrationExecutionTimelineProps) {
	const { t } = useTranslation();

	if (isLoading) {
		return <Text c="dimmed">{t("pages.integrations.executions.list.loading", "Loading…")}</Text>;
	}

	if (events.length === 0) {
		return (
			<EmptyState
				message={t("pages.integrations.executions.detail.noEvents", "No events recorded for this execution.")}
				data-testid="integration-execution-timeline-empty"
			/>
		);
	}

	return (
		<Stack gap="xs" data-testid="integration-execution-timeline">
			{events.map((event) => {
				const detail = parseDetail(event.detailJson);
				const line = detailLine(event.eventType, detail);

				return (
					<Stack key={event.sequence} gap={4} data-testid={`integration-execution-event-${event.sequence}`}>
						<Group gap="sm" wrap="nowrap">
							<Text size="xs" c="dimmed" ff="monospace">
								{event.sequence}
							</Text>
							<Badge variant="outline" color="grape">
								{event.eventType}
							</Badge>
							<Text size="xs" c="dimmed">
								{formatIntegrationTimestamp(event.occurredAtUtc)}
							</Text>
							{line === null ? null : <Text size="sm">{line}</Text>}
						</Group>
						{event.eventType === "external.output" ? (
							<CodeEditor
								value={formatOutputPayload(detail, event.detailJson)}
								language="json"
								readOnly={true}
								height={240}
								aria-label={t("pages.integrations.executions.detail.outputTitle", "Output payload")}
								data-testid="integration-execution-output"
							/>
						) : null}
					</Stack>
				);
			})}
		</Stack>
	);
}
