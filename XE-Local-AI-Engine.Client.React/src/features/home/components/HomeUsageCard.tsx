import { Group, SimpleGrid, Skeleton, Text } from "@mantine/core";
import { IconChartHistogram } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { getAgentUsageSummaryOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { formatCostUsd, formatCount, formatTokensCompact } from "@/core/formatting/NumberFormatting";
import { ButtonLink } from "@/core/ui/components/ButtonLink/ButtonLink";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { StatTile } from "@/core/ui/components/StatTile/StatTile";
import { DEFAULT_RANGE_DAYS, defaultDateRange, toQueryRange } from "@/core/usage/UsageDateRange";

// The usage dashboard's default window at a glance. The query asks for the full default window; rows older than the
// retention window are already gone, so the label names the shorter of the two rather than refetching. The ledger
// holds runs persisted as chat replies (interactive chat turns and integration runs), not scheduled agent runs, graph
// agent steps, the model proxy or Model Lab, so the label says chat and integration runs.
export function HomeUsageCard() {
	const { t } = useTranslation();
	// Stamped once per mount so a re-render never shifts the window and re-keys the query.
	const [range] = useState(() => toQueryRange(defaultDateRange(Date.now())));
	const summary = useQuery(getAgentUsageSummaryOptions({ query: range }));
	const days = Math.min(DEFAULT_RANGE_DAYS, summary.data?.retentionDays ?? DEFAULT_RANGE_DAYS);
	const totals = summary.data?.totals;

	return (
		<SectionCard
			title={t("pages.home.usage.title", "Usage")}
			icon={<IconChartHistogram size={22} />}
			actions={
				<ButtonLink to={nodeRoutePaths.usage} variant="subtle" size="compact-sm" data-testid="home-usage-link">
					{t("pages.home.usage.open", "Open usage")}
				</ButtonLink>
			}
			data-testid="home-usage"
		>
			<Text c="dimmed" size="sm">
				{t("pages.home.usage.scope", "Chat and integration runs, last {{days}} days", { days })}
			</Text>
			{summary.isLoading ? (
				<Group grow={true}>
					<Skeleton height={64} />
					<Skeleton height={64} />
					<Skeleton height={64} />
				</Group>
			) : summary.isError ? (
				<InlineErrorAlert
					message={apiErrorMessage(summary.error, t("pages.home.usage.loadError", "Usage could not be loaded."))}
					data-testid="home-usage-error"
				/>
			) : totals === undefined || totals.runCount === 0 ? (
				<Text size="sm" data-testid="home-usage-empty">
					{t("pages.home.usage.empty", "No runs recorded in this window.")}
				</Text>
			) : (
				<SimpleGrid cols={{ base: 1, xs: 3 }} spacing="sm">
					<StatTile
						variant="paper"
						label={t("pages.home.usage.totalTokens", "Total tokens")}
						value={formatTokensCompact(totals.totalTokens)}
						exactValue={formatCount(totals.totalTokens)}
						valueTestId="home-usage-total-tokens"
					/>
					<StatTile
						variant="paper"
						label={t("pages.home.usage.runs", "Runs")}
						value={formatCount(totals.runCount)}
						valueTestId="home-usage-runs"
					/>
					<StatTile
						variant="paper"
						label={t("pages.home.usage.estimatedCost", "Est. cost")}
						value={formatCostUsd(totals.estimatedCostUsd)}
						valueTestId="home-usage-cost"
					/>
				</SimpleGrid>
			)}
		</SectionCard>
	);
}
