import { Card, SimpleGrid, Stack, Text, Tooltip } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { UsageTotalsDto } from "@/features/usage-dashboard/models/UsageDashboardModel";
import { formatCostUsd, formatCount, formatTokensCompact } from "@/features/usage-dashboard/models/UsageDashboardModel";

interface StatCardProps {
	readonly label: string;
	readonly value: number;
	readonly testId: string;
	// The headline display for `value`. Defaults to the compact token format; the cost tile passes the USD formatter.
	readonly formatValue?: (value: number) => string;
	// The exact value surfaced on hover/aria. Defaults to the grouped full count; the cost tile reuses its USD format
	// (the currency value is already exact, so there is no separate "compact vs full" distinction).
	readonly formatExact?: (value: number) => string;
}

// A single stat tile: compact headline value with the exact value available on hover/aria for accessibility.
function StatCard({ label, value, testId, formatValue = formatTokensCompact, formatExact = formatCount }: StatCardProps) {
	const exact = formatExact(value);
	return (
		<Card withBorder={true} radius="md" p="lg" data-testid={testId}>
			<Stack gap={4}>
				<Text size="sm" c="dimmed">
					{label}
				</Text>
				<Tooltip label={exact} withArrow={true}>
					<Text size="xl" fw={700} aria-label={exact} data-testid={`${testId}-value`}>
						{formatValue(value)}
					</Text>
				</Tooltip>
			</Stack>
		</Card>
	);
}

// The grand-totals row: total tokens plus the prompt / completion / reasoning split, the run count, and the estimated
// cost. The cost figure is the server-computed total (never recomputed here); it reads "—" when the range was entirely
// free/local/unpriced usage.
export function UsageTotalsCards({ totals }: { readonly totals: UsageTotalsDto }) {
	const { t } = useTranslation();
	return (
		<SimpleGrid cols={{ base: 1, xs: 2, md: 6 }} spacing="md" data-testid="usage-totals">
			<StatCard label={t("pages.usage.totals.totalTokens", "Total tokens")} value={totals.totalTokens} testId="usage-total-tokens" />
			<StatCard label={t("pages.usage.totals.promptTokens", "Prompt tokens")} value={totals.promptTokens} testId="usage-prompt-tokens" />
			<StatCard
				label={t("pages.usage.totals.completionTokens", "Completion tokens")}
				value={totals.completionTokens}
				testId="usage-completion-tokens"
			/>
			<StatCard
				label={t("pages.usage.totals.reasoningTokens", "Reasoning tokens")}
				value={totals.reasoningTokens}
				testId="usage-reasoning-tokens"
			/>
			<StatCard label={t("pages.usage.totals.runCount", "Runs")} value={totals.runCount} testId="usage-run-count" />
			<StatCard
				label={t("pages.usage.totals.estimatedCost", "Est. cost")}
				value={totals.estimatedCostUsd}
				testId="usage-estimated-cost"
				formatValue={formatCostUsd}
				formatExact={formatCostUsd}
			/>
		</SimpleGrid>
	);
}
