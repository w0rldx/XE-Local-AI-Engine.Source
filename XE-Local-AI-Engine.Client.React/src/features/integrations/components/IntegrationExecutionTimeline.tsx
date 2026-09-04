import { Badge, Button, Code, Group, Stack, Text } from "@mantine/core";
import type { TFunction } from "i18next";
import { useState } from "react";
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
 * Above this many characters an output payload gets the Monaco viewer, and only behind a disclosure. Below it a plain
 * `<Code block>` is enough: nothing caps the NUMBER of `external.output` events an execution emits, so an editor per
 * output would put dozens of them in a dialog that re-renders every 5 s.
 */
const monacoPayloadThreshold = 4096;

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

/**
 * The viewer language for one output, from the `contentType` the integrator sent. Only a JSON media type gets `json`:
 * S3 stores the payload verbatim, so a `text/plain` body is not JSON and must not be shown as if it were.
 */
function outputLanguage(contentType: string | undefined): string {
	const mediaType = (contentType ?? "").split(";", 1).join("").trim().toLowerCase();
	return mediaType === "application/json" || mediaType === "text/json" || mediaType.endsWith("+json")
		? "json"
		: "plaintext";
}

/** Pretty-prints the caller's payload for the read-only viewer, falling back to the raw text when it will not parse. */
function formatOutputPayload(detail: EventDetail | null, detailJson: string | null): string {
	if (detail === null) {
		return detailJson ?? "";
	}
	// A `payload` key that is present but null is a legitimate payload of `null`, not a missing one, so only a shape
	// carrying no `payload` at all falls back to showing the whole envelope.
	if (!("payload" in detail)) {
		return JSON.stringify(detail, null, 2);
	}
	// A non-JSON body is already text: stringifying it would show the operator a quoted, escaped string.
	if (typeof detail.payload === "string" && outputLanguage(detail.contentType) !== "json") {
		return detail.payload;
	}
	return JSON.stringify(detail.payload, null, 2);
}

function detailLine(eventType: string, detail: EventDetail | null, t: TFunction): string | null {
	if (detail === null) {
		return null;
	}

	switch (eventType) {
		case "tool.started":
			return detail.name ?? null;
		case "tool.completed": {
			if (detail.name === undefined) {
				return null;
			}
			// "ok" and "error" are this client's vocabulary, not the backend's, so unlike the failure category below
			// they are translated.
			const outcome =
				detail.ok === false
					? t("pages.integrations.executions.detail.toolError", "error")
					: t("pages.integrations.executions.detail.toolOk", "ok");
			return `${detail.name} — ${outcome}`;
		}
		case "execution.completed": {
			const duration = detail.durationMs === undefined ? null : `${(detail.durationMs / 1000).toFixed(1)}s`;
			// `tokens` is omitted entirely when the provider reported no usage, so it is never rendered as a null.
			const tokens =
				detail.tokens === undefined
					? null
					: t("pages.integrations.executions.detail.tokens", {
							defaultValue: "{{count}} tokens",
							count: detail.tokens,
						});
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
 * One `external.output` payload. A small one renders as plain text; a large one stays behind a disclosure, so the
 * heavyweight editor is mounted only for the output the operator actually opened.
 */
function OutputPayload({ text, language }: { text: string; language: string }) {
	const { t } = useTranslation();
	const [opened, setOpened] = useState(false);

	const label = t("pages.integrations.executions.detail.outputTitle", "Output payload");

	if (text.length <= monacoPayloadThreshold) {
		return (
			<Code
				block={true}
				aria-label={label}
				data-testid="integration-execution-output"
				style={{ maxHeight: 240, overflow: "auto" }}
			>
				{text}
			</Code>
		);
	}

	return (
		<Stack gap={4}>
			<Button
				variant="subtle"
				size="xs"
				onClick={() => setOpened((current) => !current)}
				data-testid="integration-execution-output-toggle"
			>
				{opened
					? t("pages.integrations.executions.detail.hideOutput", "Hide output payload")
					: t("pages.integrations.executions.detail.showOutput", "Show output payload")}
			</Button>
			{opened ? (
				<CodeEditor
					value={text}
					language={language}
					readOnly={true}
					height={240}
					aria-label={label}
					data-testid="integration-execution-output"
				/>
			) : null}
		</Stack>
	);
}

/**
 * The persisted event list of one execution. Sequence values ascend but may SKIP — a failed durable write leaves a
 * permanent hole — so a gap is rendered as the two rows it is, never as a missing row or a loading state.
 */
export function IntegrationExecutionTimeline({ events, isLoading }: IntegrationExecutionTimelineProps) {
	const { t } = useTranslation();

	if (isLoading) {
		return <Text c="dimmed">{t("pages.integrations.executions.detail.loading", "Loading timeline…")}</Text>;
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
				const line = detailLine(event.eventType, detail, t);

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
							<OutputPayload
								text={formatOutputPayload(detail, event.detailJson)}
								language={outputLanguage(detail?.contentType)}
							/>
						) : null}
					</Stack>
				);
			})}
		</Stack>
	);
}
