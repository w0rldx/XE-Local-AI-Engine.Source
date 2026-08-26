import { Card, Group, Stack, Table, Text, Title } from "@mantine/core";
import { DonutChart } from "@mantine/charts";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import type { ProviderTotalsDto } from "@/features/usage-dashboard/models/UsageDashboardModel";
import { formatCostUsd, formatCount, formatTokensCompact } from "@/features/usage-dashboard/models/UsageDashboardModel";
import { providerColor, providerLabel } from "@/features/usage-dashboard/models/UsageProviderPresentation";

// Per-provider breakdown: a token-share donut beside a compact per-provider table (runs + total tokens). Providers
// with zero tokens are dropped from the donut (a zero slice renders nothing) but kept in the table so their run
// count is still visible.
export function UsageProviderBreakdown({ byProvider }: { readonly byProvider: readonly ProviderTotalsDto[] }) {
	const { t } = useTranslation();

	const donutData = useMemo(
		() =>
			byProvider
				.filter((entry) => entry.totalTokens > 0)
				.map((entry) => ({
					name: providerLabel(entry.provider, t),
					value: entry.totalTokens,
					color: providerColor(entry.provider),
				})),
		[byProvider, t],
	);

	const rows = useMemo(
		() => byProvider.toSorted((a, b) => b.totalTokens - a.totalTokens),
		[byProvider],
	);

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="usage-provider-breakdown">
			<Stack gap="md">
				<Title order={3}>{t("pages.usage.providers.title", "Usage by provider")}</Title>
				<Group align="flex-start" gap="xl" wrap="wrap">
					{donutData.length > 0 ? (
						<DonutChart
							data={donutData}
							size={200}
							thickness={28}
							withTooltip={true}
							valueFormatter={formatTokensCompact}
							data-testid="usage-provider-donut"
						/>
					) : (
						<Text c="dimmed">{t("pages.usage.providers.noTokens", "No token usage recorded for any provider in this range.")}</Text>
					)}
					{/*
					 * `minWidth` on the table itself only made the four columns overflow the card silently. Moving the
					 * floor onto a ScrollContainer keeps the same minimum but gives the overflow somewhere to go, so on a
					 * phone the cost column is reachable by scrolling instead of clipped.
					 */}
					<Table.ScrollContainer minWidth={280} style={{ flex: 1, minWidth: 0 }}>
						<Table verticalSpacing="xs">
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("pages.usage.providers.columns.provider", "Provider")}</Table.Th>
									<Table.Th>{t("pages.usage.providers.columns.runs", "Runs")}</Table.Th>
									<Table.Th>{t("pages.usage.providers.columns.totalTokens", "Total tokens")}</Table.Th>
									<Table.Th>{t("pages.usage.providers.columns.estimatedCost", "Est. cost")}</Table.Th>
								</Table.Tr>
							</Table.Thead>
							<Table.Tbody>
								{rows.map((entry) => (
									<Table.Tr key={entry.provider} data-testid={`usage-provider-row-${entry.provider}`}>
										<Table.Td>{providerLabel(entry.provider, t)}</Table.Td>
										<Table.Td>{formatCount(entry.runCount)}</Table.Td>
										<Table.Td>
											<Text aria-label={formatCount(entry.totalTokens)}>{formatTokensCompact(entry.totalTokens)}</Text>
										</Table.Td>
										<Table.Td>
											<Text data-testid={`usage-provider-cost-${entry.provider}`}>{formatCostUsd(entry.estimatedCostUsd)}</Text>
										</Table.Td>
									</Table.Tr>
								))}
							</Table.Tbody>
						</Table>
					</Table.ScrollContainer>
				</Group>
			</Stack>
		</Card>
	);
}
