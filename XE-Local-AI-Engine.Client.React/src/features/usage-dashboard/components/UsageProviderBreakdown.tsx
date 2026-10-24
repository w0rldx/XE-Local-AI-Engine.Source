import { DonutChart } from "@mantine/charts";
import { Card, Group, Stack, Text, Title } from "@mantine/core";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import type { ProviderTotalsDto } from "@/features/usage-dashboard/models/UsageDashboardModel";
import { formatCostUsd, formatCount, formatTokensCompact } from "@/features/usage-dashboard/models/UsageDashboardModel";
import { providerColor, providerLabel } from "@/features/usage-dashboard/models/UsageProviderPresentation";

// The width the four columns need; also the table's flex basis beside the donut.
const PROVIDER_TABLE_MIN_WIDTH = 360;

// Per-provider breakdown: a token-share donut beside a compact per-provider table (runs + total tokens). Providers
// with zero tokens are dropped from the donut (a zero slice renders nothing) but kept in the table so their run
// count is still visible.
export function UsageProviderBreakdown({
	byProvider,
	externalConnectionNames,
}: {
	readonly byProvider: readonly ProviderTotalsDto[];
	// External connection id → display name, resolved by the page. Absent ids render as the bare "External".
	readonly externalConnectionNames?: ReadonlyMap<string, string>;
}) {
	const { t } = useTranslation();

	const donutData = useMemo(
		() =>
			byProvider
				.filter((entry) => entry.totalTokens > 0)
				.map((entry) => ({
					name: providerLabel(entry.provider, t, externalConnectionNames),
					value: entry.totalTokens,
					color: providerColor(entry.provider),
				})),
		[byProvider, externalConnectionNames, t],
	);

	const rows = useMemo(() => byProvider.toSorted((a, b) => b.totalTokens - a.totalTokens), [byProvider]);

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="usage-provider-breakdown">
			<Stack gap="md">
				<Title order={2} size="h3">
					{t("pages.usage.providers.title", "Usage by provider")}
				</Title>
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
						<Text c="dimmed">
							{t("pages.usage.providers.noTokens", "No token usage recorded for any provider in this range.")}
						</Text>
					)}
					{/*
					 * The basis is the table's own minimum: when the donut and the table no longer fit side by side, the
					 * table wraps below the donut instead of being squeezed beside it, and only a container too narrow
					 * even for that turns it into cards.
					 */}
					<div style={{ flex: `1 1 ${PROVIDER_TABLE_MIN_WIDTH}px`, minWidth: 0 }}>
						<ResponsiveTable
							rows={rows}
							rowKey={(entry) => entry.provider}
							rowTestId={(entry) => `usage-provider-row-${entry.provider}`}
							minWidth={PROVIDER_TABLE_MIN_WIDTH}
							columns={[
								{
									key: "provider",
									header: t("pages.usage.providers.columns.provider", "Provider"),
									primary: true,
									cell: (entry) => providerLabel(entry.provider, t, externalConnectionNames),
								},
								{
									key: "runs",
									header: t("pages.usage.providers.columns.runs", "Runs"),
									cell: (entry) => formatCount(entry.runCount),
								},
								{
									key: "totalTokens",
									header: t("pages.usage.providers.columns.totalTokens", "Total tokens"),
									cell: (entry) => (
										<Text aria-label={formatCount(entry.totalTokens)}>{formatTokensCompact(entry.totalTokens)}</Text>
									),
								},
								{
									key: "estimatedCost",
									header: t("pages.usage.providers.columns.estimatedCost", "Est. cost"),
									cell: (entry) => (
										<Text data-testid={`usage-provider-cost-${entry.provider}`}>{formatCostUsd(entry.estimatedCostUsd)}</Text>
									),
								},
							]}
						/>
					</div>
				</Group>
			</Stack>
		</Card>
	);
}
