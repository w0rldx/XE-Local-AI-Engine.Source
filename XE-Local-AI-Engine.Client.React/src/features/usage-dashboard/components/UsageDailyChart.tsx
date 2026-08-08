import { Card, Stack, Text, Title } from "@mantine/core";
import { AreaChart } from "@mantine/charts";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import type { UsageDailyPoint } from "@/features/usage-dashboard/models/UsageDashboardModel";
import { formatDayLabel, formatTokensCompact } from "@/features/usage-dashboard/models/UsageDashboardModel";

// Daily total-tokens time series. `daily` is expected pre-aggregated + ascending by day (see aggregateByDay).
export function UsageDailyChart({ daily }: { readonly daily: readonly UsageDailyPoint[] }) {
	const { t } = useTranslation();

	const chartData = useMemo(
		() => daily.map((point) => ({ day: formatDayLabel(point.dayStartUtcMs), totalTokens: point.totalTokens })),
		[daily],
	);

	return (
		<Card withBorder={true} radius="md" p="lg" data-testid="usage-daily-chart">
			<Stack gap="md">
				<Title order={3}>{t("pages.usage.daily.title", "Daily token usage")}</Title>
				{chartData.length > 0 ? (
					<AreaChart
						h={280}
						data={chartData}
						dataKey="day"
						withDots={chartData.length <= 31}
						valueFormatter={formatTokensCompact}
						series={[{ name: "totalTokens", label: t("pages.usage.daily.seriesLabel", "Total tokens"), color: "blue.6" }]}
						curveType="monotone"
						data-testid="usage-daily-area"
					/>
				) : (
					<Text c="dimmed">{t("pages.usage.daily.empty", "No daily usage to plot for this range.")}</Text>
				)}
			</Stack>
		</Card>
	);
}
