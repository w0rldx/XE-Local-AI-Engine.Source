import { Card, SimpleGrid, Stack, Text, Tooltip } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { UsageTotalsDto } from "@/features/usage-dashboard/models/UsageDashboardModel";
import { formatCount, formatTokensCompact } from "@/features/usage-dashboard/models/UsageDashboardModel";

interface StatCardProps {
	readonly label: string;
	readonly value: number;
	readonly testId: string;
}

// A single stat tile: compact headline value with the exact grouped count available on hover/aria for accessibility.
function StatCard({ label, value, testId }: StatCardProps) {
	const exact = formatCount(value);
	return (
		<Card withBorder={true} radius="md" p="lg" data-testid={testId}>
			<Stack gap={4}>
				<Text size="sm" c="dimmed">
					{label}
				</Text>
				<Tooltip label={exact} withArrow={true}>
					<Text size="xl" fw={700} aria-label={exact} data-testid={`${testId}-value`}>
						{formatTokensCompact(value)}
					</Text>
				</Tooltip>
			</Stack>
		</Card>
	);
}

// The grand-totals row: total tokens plus the prompt / completion / reasoning split and the run count.
export function UsageTotalsCards({ totals }: { readonly totals: UsageTotalsDto }) {
	const { t } = useTranslation();
	return (
		<SimpleGrid cols={{ base: 1, xs: 2, md: 5 }} spacing="md" data-testid="usage-totals">
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
		</SimpleGrid>
	);
}
