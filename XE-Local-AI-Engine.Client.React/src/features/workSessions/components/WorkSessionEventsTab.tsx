import { Alert, Anchor, Badge, Button, Code, Collapse, Group, Paper, Stack, Text } from "@mantine/core";
import type { TFunction } from "i18next";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import type { WorkSessionEventResponse } from "@/features/workSessions/models/WorkSessionModels";

/** The two event types whose `detailJson` carries the step's consumption record. Every other type is rendered as-is. */
const consumptionEventTypes = new Set(["StepEnded", "StepFailed"]);

/**
 * What one step spent, as the supervisor writes it onto its StepEnded/StepFailed row. Deliberately a narrow read: the
 * `detailJson` column is opaque and other writers use it for their own shapes, so anything that does not carry the
 * four required counts renders nothing extra rather than a half-filled line.
 */
interface StepConsumption {
	readonly providerCalls: number;
	readonly estimatedInputTokens: number;
	readonly toolCallsCompleted: number;
	readonly providerCallCap: number;
	/** How many invocations ran under the step's cap scope. Absent on rows written before the field existed; read as 1. */
	readonly attachedBudgets: number;
}

function isFiniteNumber(value: unknown): value is number {
	return typeof value === "number" && Number.isFinite(value);
}

function parseConsumption(eventType: string | null | undefined, detailJson: string | null | undefined): StepConsumption | undefined {
	if (!eventType || !consumptionEventTypes.has(eventType) || !detailJson) {
		return undefined;
	}

	let parsed: unknown;
	try {
		parsed = JSON.parse(detailJson);
	} catch {
		// A row written by something else, or truncated in transit. The raw payload is still one click away.
		return undefined;
	}

	if (typeof parsed !== "object" || parsed === null) {
		return undefined;
	}

	const candidate = parsed as Record<string, unknown>;
	if (
		!isFiniteNumber(candidate["providerCalls"]) ||
		!isFiniteNumber(candidate["estimatedInputTokens"]) ||
		!isFiniteNumber(candidate["toolCallsCompleted"]) ||
		!isFiniteNumber(candidate["providerCallCap"])
	) {
		return undefined;
	}

	return {
		providerCalls: candidate["providerCalls"],
		estimatedInputTokens: candidate["estimatedInputTokens"],
		toolCallsCompleted: candidate["toolCallsCompleted"],
		providerCallCap: candidate["providerCallCap"],
		// Not required: a row written before this field existed is still a usable record, and one budget is what it
		// described. Anything non-numeric reads as the ordinary single-invocation case rather than voiding the row.
		attachedBudgets: isFiniteNumber(candidate["attachedBudgets"]) ? candidate["attachedBudgets"] : 1,
	};
}

/**
 * Token counts read as magnitudes here, not as figures: 18,247 becomes "18.2k" so the line stays one glance wide.
 *
 * Both halves are locale-driven. `Intl.NumberFormat` on the active i18next language supplies the decimal separator —
 * hardcoding `toFixed(1)` printed "18.2k" to a German reader, who writes "18,2k" — and the thousands suffix comes from
 * the translation catalogue rather than from this file, so a locale that does not abbreviate with "k" can say so.
 */
function formatTokens(value: number, language: string, t: TFunction): string {
	const rounded = Math.max(0, Math.round(value));
	if (rounded < 1000) {
		return new Intl.NumberFormat(language).format(rounded);
	}

	const thousands = new Intl.NumberFormat(language, { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(rounded / 1000);
	return t("pages.workSessions.events.tokensThousands", "{{value}}k", { value: thousands });
}

export interface WorkSessionEventsTabProps {
	readonly events: readonly WorkSessionEventResponse[];
	/** The server has more events beyond the current page. */
	readonly hasMore: boolean;
	/** False once the page size has reached the server's clamp — asking for more would silently return the same page. */
	readonly canLoadMore: boolean;
	readonly onLoadMore: () => void;
}

export function WorkSessionEventsTab({ events, hasMore, canLoadMore, onLoadMore }: WorkSessionEventsTabProps) {
	const { t, i18n } = useTranslation();
	const [expandedId, setExpandedId] = useState<string | undefined>(undefined);

	if (events.length === 0) {
		return (
			<Alert color="gray" variant="light" data-testid="work-session-events-empty">
				{t("pages.workSessions.events.empty", "Nothing has happened in this session yet.")}
			</Alert>
		);
	}

	const ordered = events.toSorted((left, right) => (right.sequence ?? 0) - (left.sequence ?? 0));

	function consumptionLine(event: WorkSessionEventResponse): string | undefined {
		const consumption = parseConsumption(event.eventType, event.detailJson);
		if (!consumption) {
			return undefined;
		}

		// Every figure here is a step TOTAL. The provider's own reported usage is deliberately absent: it is a
		// per-round reading, so beside these it would contradict them on any multi-round step.
		const tokens = formatTokens(consumption.estimatedInputTokens, i18n.language, t);
		if (consumption.attachedBudgets > 1) {
			// The cap bounds each invocation, not their sum, so a step that spawned sub-agents has no single ratio to
			// show. Rendering "18/10" here would read as a breached cap and argue for raising one nothing hit.
			return t(
				"pages.workSessions.events.consumptionAcrossBudgets",
				"{{calls}} provider calls across {{budgets}} budgets (cap {{cap}} each) · {{tools}} tool calls · ~{{tokens}} est. input tokens",
				{
					calls: consumption.providerCalls,
					budgets: consumption.attachedBudgets,
					cap: consumption.providerCallCap,
					tools: consumption.toolCallsCompleted,
					tokens,
				},
			);
		}

		return t("pages.workSessions.events.consumption", "{{calls}}/{{cap}} provider calls · {{tools}} tool calls · ~{{tokens}} est. input tokens", {
			calls: consumption.providerCalls,
			cap: consumption.providerCallCap,
			tools: consumption.toolCallsCompleted,
			tokens,
		});
	}

	const rows = ordered.map((event) => ({ event, consumption: consumptionLine(event) }));

	return (
		<Stack gap="xs" data-testid="work-session-events-tab">
			{rows.map(({ event, consumption }) => (
				<Paper key={event.id} withBorder={true} p="xs" data-testid={`work-session-event-${event.id}`}>
					<Stack gap={4}>
						<Group gap="xs" wrap="nowrap">
							<Badge size="xs" variant="light">
								{event.eventType}
							</Badge>
							<Text size="xs" c="dimmed" style={{ flex: 1, minWidth: 0 }}>
								{t("pages.workSessions.events.meta", "step {{step}} · #{{sequence}}", {
									step: event.step ?? 0,
									sequence: event.sequence ?? 0,
								})}
							</Text>
							<Text size="xs" c="dimmed">
								{new Date(event.occurredAtUtc ?? 0).toLocaleTimeString()}
							</Text>
						</Group>
						{event.outcome || consumption ? (
							<Group gap="xs" wrap="wrap">
								{event.outcome ? (
									<Text size="xs" data-testid={`work-session-event-outcome-${event.id}`}>
										{event.outcome}
									</Text>
								) : null}
								{consumption ? (
									<Text size="xs" c="dimmed" data-testid={`work-session-event-consumption-${event.id}`}>
										{consumption}
									</Text>
								) : null}
							</Group>
						) : null}
						{event.detailJson ? (
							<>
								<Anchor
									component="button"
									type="button"
									size="xs"
									onClick={() => setExpandedId((current) => (current === event.id ? undefined : event.id))}
									data-testid={`work-session-event-toggle-${event.id}`}
								>
									{expandedId === event.id
										? t("pages.workSessions.events.hideDetail", "Hide detail")
										: t("pages.workSessions.events.showDetail", "Show detail")}
								</Anchor>
								<Collapse expanded={expandedId === event.id}>
									<Code block={true} data-testid={`work-session-event-detail-${event.id}`}>
										{event.detailJson}
									</Code>
								</Collapse>
							</>
						) : null}
					</Stack>
				</Paper>
			))}
			{hasMore ? (
				canLoadMore ? (
					<Button size="xs" variant="light" onClick={onLoadMore} data-testid="work-session-events-load-more">
						{t("pages.workSessions.events.loadMore", "Load more")}
					</Button>
				) : (
					<Text size="xs" c="dimmed" data-testid="work-session-events-truncated">
						{t("pages.workSessions.events.truncated", "Only the first events are shown.")}
					</Text>
				)
			) : null}
		</Stack>
	);
}
