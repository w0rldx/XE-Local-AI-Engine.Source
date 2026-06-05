import { Alert, Badge, Group, Loader, Paper, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle, IconThumbDown, IconThumbUp } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { FeedbackExemplar, OverallFeedback, ToolFeedbackBreakdown } from "@/features/agents/models/FeedbackInsightsModels";
import { useFeedbackInsights } from "@/features/agents/queries/useFeedbackInsights";

interface FeedbackInsightsPanelProps {
	// The agent whose feedback aggregate is shown. Rendered by the parent only when agentManagement is on; it
	// also guards internally so it can never render its surface when the capability is off.
	agentDefinitionId: string;
	agentName: string;
	// FE-static capability gate (folded under agentManagement). When false the panel renders nothing.
	enabled: boolean;
}

function errorMessage(error: unknown, fallback: string): string {
	return error instanceof Error ? error.message : fallback;
}

// Render a fraction (0..1) as a whole-percent string for display.
function toPercent(fraction: number): string {
	return `${Math.round(fraction * 100)}%`;
}

// Per-agent read-only feedback insights panel. Aggregates the up/down ratings and verbatim
// comment exemplars already persisted node-locally for this agent: an overall up/down split with a down-rate,
// a per-tool breakdown table (conversation-level attribution — see the footnote), and a capped list of comment
// exemplars. Rows below the occurrence threshold carry a de-emphasized "not enough signal" label so an
// operator never acts on n=1. Capability-gated under agentManagement — when `enabled` is false it renders
// nothing. Read-only: no mutations.
export function FeedbackInsightsPanel({ agentDefinitionId, agentName, enabled }: FeedbackInsightsPanelProps) {
	const { t } = useTranslation();

	const insightsQuery = useFeedbackInsights(enabled ? agentDefinitionId : null);

	if (!enabled) {
		return null;
	}

	const insights = insightsQuery.data;
	const threshold = insights?.minOccurrenceThreshold ?? 0;

	return (
		<Paper withBorder={true} radius="md" p="md" data-testid={`feedback-insights-panel-${agentDefinitionId}`}>
			<Stack gap="sm">
				<Stack gap={2}>
					<Text fw={600}>{t("pages.agents.insights.title", "Feedback insights")}</Text>
					<Text size="xs" c="dimmed">
						{t(
							"pages.agents.insights.subtitle",
							"Read-only summary of the up/down feedback and comments collected for {{name}}.",
							{ name: agentName },
						)}
					</Text>
				</Stack>

				{insightsQuery.isLoading ? (
					<Group gap="sm" data-testid="feedback-insights-loading">
						<Loader size="sm" />
						<Text c="dimmed" size="sm">
							{t("pages.agents.insights.loading", "Loading feedback insights…")}
						</Text>
					</Group>
				) : null}

				{insightsQuery.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="feedback-insights-error">
						{errorMessage(insightsQuery.error, t("pages.agents.insights.errors.load", "Could not load feedback insights."))}
					</Alert>
				) : null}

				{insights ? (
					<FeedbackInsightsContent
						overall={insights.overall}
						byTool={insights.byTool}
						exemplars={insights.exemplars}
						threshold={threshold}
					/>
				) : null}
			</Stack>
		</Paper>
	);
}

interface FeedbackInsightsContentProps {
	overall: OverallFeedback;
	byTool: readonly ToolFeedbackBreakdown[];
	exemplars: readonly FeedbackExemplar[];
	threshold: number;
}

function FeedbackInsightsContent({ overall, byTool, exemplars, threshold }: FeedbackInsightsContentProps) {
	const { t } = useTranslation();

	if (overall.total === 0) {
		return (
			<Text size="sm" c="dimmed" data-testid="feedback-insights-empty">
				{t("pages.agents.insights.empty", "No feedback collected for this agent yet.")}
			</Text>
		);
	}

	return (
		<Stack gap="md">
			<Group gap="lg" align="center" data-testid="feedback-insights-overall">
				<Group gap={6} align="center">
					<IconThumbUp size={16} />
					<Text size="sm" data-testid="feedback-insights-up">
						{t("pages.agents.insights.up", "Up {{count}}", { count: overall.up })}
					</Text>
				</Group>
				<Group gap={6} align="center">
					<IconThumbDown size={16} />
					<Text size="sm" data-testid="feedback-insights-down">
						{t("pages.agents.insights.down", "Down {{count}}", { count: overall.down })}
					</Text>
				</Group>
				<Text size="sm" c="dimmed" data-testid="feedback-insights-down-rate">
					{t("pages.agents.insights.downRate", "Down rate {{rate}}", { rate: toPercent(overall.downRate) })}
				</Text>
				{!overall.meetsThreshold ? (
					<NotEnoughSignalLabel threshold={threshold} testId="feedback-insights-overall-threshold" />
				) : null}
			</Group>

			<Stack gap={4}>
				<Text size="xs" fw={600} c="dimmed">
					{t("pages.agents.insights.perTool", "By tool")}
				</Text>
				{byTool.length === 0 ? (
					<Text size="xs" c="dimmed" data-testid="feedback-insights-tools-empty">
						{t("pages.agents.insights.toolsEmpty", "No tool-attributed feedback yet.")}
					</Text>
				) : (
					<Table data-testid="feedback-insights-tools">
						<Table.Thead>
							<Table.Tr>
								<Table.Th>{t("pages.agents.insights.toolHeader", "Tool")}</Table.Th>
								<Table.Th>{t("pages.agents.insights.upHeader", "Up")}</Table.Th>
								<Table.Th>{t("pages.agents.insights.downHeader", "Down")}</Table.Th>
								<Table.Th>{t("pages.agents.insights.countHeader", "Total")}</Table.Th>
								<Table.Th>{t("pages.agents.insights.downRateHeader", "Down rate")}</Table.Th>
							</Table.Tr>
						</Table.Thead>
						<Table.Tbody>
							{byTool.map((tool) => (
								<Table.Tr key={tool.toolName} data-testid={`feedback-insights-tool-${tool.toolName}`}>
									<Table.Td>
										<Group gap="xs" align="center" wrap="wrap">
											<Text size="sm">{tool.toolName}</Text>
											{!tool.meetsThreshold ? (
												<NotEnoughSignalLabel
													threshold={threshold}
													testId={`feedback-insights-tool-threshold-${tool.toolName}`}
												/>
											) : null}
										</Group>
									</Table.Td>
									<Table.Td>{tool.up}</Table.Td>
									<Table.Td>{tool.down}</Table.Td>
									<Table.Td>{tool.total}</Table.Td>
									<Table.Td>{toPercent(tool.downRate)}</Table.Td>
								</Table.Tr>
							))}
						</Table.Tbody>
					</Table>
				)}
				<Text size="xs" c="dimmed" data-testid="feedback-insights-attribution">
					{t(
						"pages.agents.insights.attributionFootnote",
						"Per-tool counts are conversation-level attribution: feedback on messages in conversations where the tool was used, not on the specific message that called it.",
					)}
				</Text>
			</Stack>

			<Stack gap={4}>
				<Text size="xs" fw={600} c="dimmed">
					{t("pages.agents.insights.exemplars", "Recent comments")}
				</Text>
				{exemplars.length === 0 ? (
					<Text size="xs" c="dimmed" data-testid="feedback-insights-exemplars-empty">
						{t("pages.agents.insights.exemplarsEmpty", "No comments left yet.")}
					</Text>
				) : (
					<Stack gap={6} data-testid="feedback-insights-exemplars">
						{exemplars.map((exemplar) => (
							<Paper
								withBorder={true}
								p="xs"
								key={exemplar.messageId}
								data-testid={`feedback-insights-exemplar-${exemplar.messageId}`}
							>
								<Group gap="xs" align="flex-start" wrap="nowrap">
									<Badge
										size="xs"
										variant="light"
										color={exemplar.rating === "down" ? "red" : "teal"}
										data-testid={`feedback-insights-exemplar-rating-${exemplar.messageId}`}
									>
										{exemplar.rating === "down"
											? t("pages.agents.insights.ratingDown", "down")
											: t("pages.agents.insights.ratingUp", "up")}
									</Badge>
									<Text size="sm" style={{ flex: 1, minWidth: 0 }}>
										{/* The backend already appends the ellipsis when it truncates, so render verbatim. */}
										{exemplar.comment}
									</Text>
								</Group>
							</Paper>
						))}
					</Stack>
				)}
			</Stack>
		</Stack>
	);
}

interface NotEnoughSignalLabelProps {
	threshold: number;
	testId: string;
}

// De-emphasized inline label flagging a row that is below the occurrence threshold ("never act on n=1").
function NotEnoughSignalLabel({ threshold, testId }: NotEnoughSignalLabelProps) {
	const { t } = useTranslation();
	return (
		<Text size="xs" c="dimmed" fs="italic" data-testid={testId}>
			{t("pages.agents.insights.notEnoughSignal", "not enough signal (n < {{threshold}})", { threshold })}
		</Text>
	);
}
