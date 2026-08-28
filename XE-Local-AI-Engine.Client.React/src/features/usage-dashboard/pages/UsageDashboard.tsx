import { Alert, Button, Card, Skeleton, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconChartBar, IconChartHistogram, IconRefresh } from "@tabler/icons-react";
import { useQuery } from "@tanstack/react-query";
import type { TFunction } from "i18next";
import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import {
	getAgentUsageSummaryOptions,
	listExternalProviderConnectionsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { UsageDailyChart } from "@/features/usage-dashboard/components/UsageDailyChart";
import { UsageDateRangeControl } from "@/features/usage-dashboard/components/UsageDateRangeControl";
import { UsageModelTable } from "@/features/usage-dashboard/components/UsageModelTable";
import { UsageProviderBreakdown } from "@/features/usage-dashboard/components/UsageProviderBreakdown";
import { UsageTotalsCards } from "@/features/usage-dashboard/components/UsageTotalsCards";
import type { UsageDateRange } from "@/features/usage-dashboard/models/UsageDashboardModel";
import {
	aggregateByDay,
	aggregateByModel,
	clampDateRange,
	defaultDateRange,
	isUsageEmpty,
	retentionFloorMs,
	startOfUtcDay,
	toQueryRange,
} from "@/features/usage-dashboard/models/UsageDashboardModel";

const FALLBACK_RETENTION_DAYS = 30;

function errorMessage(error: unknown, t: TFunction): string {
	return apiErrorMessage(error, t("pages.usage.loadError", "Usage summary could not be loaded."));
}

// Skeleton stand-in shown while the first summary loads, so the page keeps its shape instead of collapsing.
function UsageSkeleton() {
	return (
		<Stack gap="lg" data-testid="usage-skeleton">
			<Skeleton height={96} radius="md" />
			<Skeleton height={280} radius="md" />
			<Skeleton height={220} radius="md" />
			<Skeleton height={220} radius="md" />
		</Stack>
	);
}

// Empty-state guidance shown when the range has no recorded usage — points the operator at running an agent rather
// than leaving a bare blank panel.
function UsageEmptyState() {
	const { t } = useTranslation();
	return (
		<Card withBorder={true} radius="md" p="xl" data-testid="usage-empty">
			<Stack gap="xs" align="center">
				<IconChartHistogram size={40} opacity={0.6} />
				<Title order={3}>{t("pages.usage.empty.title", "No usage recorded yet")}</Title>
				<Text c="dimmed" ta="center" maw={520}>
					{t(
						"pages.usage.empty.body",
						"No agent runs fell in this date range. Run an agent from Chat or the Scheduler, then return here — token usage is recorded per model and provider as agents run.",
					)}
				</Text>
			</Stack>
		</Card>
	);
}

// Agent token-usage dashboard. Consumes the operator-gated agents/usage-summary endpoint (fine-grained per model,
// provider and UTC day) and presents grand totals, a daily time series, a per-provider breakdown and a per-model
// table over an operator-chosen date range clamped to the backend's retention window.
export function UsageDashboard() {
	const { t } = useTranslation();

	// Freeze "now" at mount so the range math (defaults, retention floor, clamping) is stable across renders.
	// Lazy-initialized once at mount: useRef ignores all but its first argument, so passing Date.now() directly would
	// re-evaluate it on every render and throw the result away. Initialize to null and stamp "now" once.
	const nowRef = useRef<number | null>(null);
	if (nowRef.current === null) {
		nowRef.current = Date.now();
	}
	const [range, setRange] = useState<UsageDateRange>(() => defaultDateRange(nowRef.current ?? Date.now()));
	const userAdjustedRef = useRef(false);
	const retentionAppliedRef = useRef(false);

	const queryRange = useMemo(() => toQueryRange(range), [range]);
	const {
		data: summary,
		isLoading,
		error,
		refetch,
		isFetching,
	} = useQuery({
		...withResponseValidation(
			getAgentUsageSummaryOptions({ query: { fromEpochMs: queryRange.fromEpochMs, toEpochMs: queryRange.toEpochMs } }),
		),
	});

	const retentionDays = summary?.retentionDays ?? FALLBACK_RETENTION_DAYS;

	// Once retention is known, narrow the initial default range if the window is shorter than the 30-day default.
	// Runs once and never after the operator has adjusted the range themselves.
	useEffect(() => {
		if (!summary || retentionAppliedRef.current || userAdjustedRef.current) {
			return;
		}
		retentionAppliedRef.current = true;
		if (summary.retentionDays < FALLBACK_RETENTION_DAYS) {
			setRange(defaultDateRange(nowRef.current ?? Date.now(), summary.retentionDays));
		}
	}, [summary]);

	const handleRangeChange = (next: UsageDateRange): void => {
		userAdjustedRef.current = true;
		setRange(clampDateRange(next, nowRef.current ?? Date.now(), retentionDays));
	};

	// Usage rows record external turns as `external:{connectionId}` — the id, never the name, because the ledger holds
	// one string per run and the name can change. The operator-facing names are resolved here, from the configuration
	// this page can already read, and handed to the presenters; a connection deleted since the run is simply absent and
	// its rows read "External". Nothing on the usage endpoint changes for this.
	const { data: externalConnections } = useQuery(withResponseValidation(listExternalProviderConnectionsOptions()));
	const externalConnectionNames = useMemo(
		() => new Map((externalConnections?.connections ?? []).map((connection) => [connection.id, connection.displayName])),
		[externalConnections],
	);

	const daily = useMemo(() => aggregateByDay(summary?.items ?? []), [summary]);
	const models = useMemo(() => aggregateByModel(summary?.items ?? []), [summary]);
	const empty = isUsageEmpty(summary);

	return (
		<PageShell>
			<PageHeader
				title={t("pages.usage.title", "Usage dashboard")}
				icon={<IconChartBar size={24} />}
				subtitle={t("pages.usage.subtitle", "Agent token usage by model and provider. Data is retained for {{days}} days.", {
					days: retentionDays,
				})}
				actions={
					<Button variant="subtle" leftSection={<IconRefresh size={16} />} onClick={() => refetch()} disabled={isFetching}>
						{t("common.refresh", "Refresh")}
					</Button>
				}
			/>

			<UsageDateRangeControl
				range={range}
				minMs={retentionFloorMs(nowRef.current ?? Date.now(), retentionDays)}
				maxMs={startOfUtcDay(nowRef.current ?? Date.now())}
				onChange={handleRangeChange}
			/>

			{error ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="usage-error">
					{errorMessage(error, t)}
				</Alert>
			) : null}

			{isLoading ? <UsageSkeleton /> : null}

			{!isLoading && !error && summary ? (
				empty ? (
					<UsageEmptyState />
				) : (
					<>
						<UsageTotalsCards totals={summary.totals} />
						<Text size="xs" c="dimmed" data-testid="usage-cost-disclaimer">
							{t(
								"pages.usage.costDisclaimer",
								"Costs are approximate estimates in USD, computed from the per-model rates configured in Node settings. Local and unpriced usage counts as free.",
							)}
						</Text>
						<UsageDailyChart daily={daily} />
						<UsageProviderBreakdown byProvider={summary.byProvider} externalConnectionNames={externalConnectionNames} />
						<UsageModelTable rows={models} externalConnectionNames={externalConnectionNames} />
					</>
				)
			) : null}
		</PageShell>
	);
}
