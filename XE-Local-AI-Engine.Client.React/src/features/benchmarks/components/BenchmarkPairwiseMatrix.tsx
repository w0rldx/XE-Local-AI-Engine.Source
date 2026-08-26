import { Alert, Group, Loader, ScrollArea, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconScale } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import type { BenchmarkComparison } from "@/features/benchmarks/models/BenchmarkModels";
import { formatPairwiseScore, groupComparisonsByPair } from "@/features/benchmarks/models/BenchmarkPairwise";
import { useBenchmarkComparisons } from "@/features/benchmarks/queries/useBenchmarks";

const shortId = (id: string): string => id.slice(0, 8);

function VerdictCell({ comparison, runAId }: { comparison: BenchmarkComparison | undefined; runAId: string }) {
	const { t } = useTranslation();
	if (comparison === undefined) {
		return <Text size="xs">—</Text>;
	}
	if (comparison.verdict === null) {
		return (
			<StatusBadge
				color={comparison.status === "Failed" ? "red" : "gray"}
				label={t(`pages.benchmarks.pairwise.status.${comparison.status}`, comparison.status)}
				inProgress={comparison.status === "Running" || comparison.status === "Queued"}
				data-testid={`benchmark-pairwise-status-${comparison.id}`}
			/>
		);
	}
	// The verdict is stored against the canonical pair, never against the order it was shown in, so it reads the same
	// in both columns — which is what makes a disagreement between the two columns visible as a disagreement.
	const winner = comparison.verdict === "tie" ? null : comparison.verdict === "a" ? runAId : comparison.runBId;
	// A verdict over a cut-off answer graded a fragment. It still counts in the fit, so the flag is the only thing
	// that stops the reader taking it for a judgement of the whole answer.
	const truncated = comparison.answerATruncated || comparison.answerBTruncated;
	return (
		<Group gap={4} wrap="nowrap">
			<StatusBadge
				color={comparison.verdict === "tie" ? "gray" : "blue"}
				label={winner === null ? t("pages.benchmarks.pairwise.tie", "tie") : shortId(winner)}
				data-testid={`benchmark-pairwise-verdict-${comparison.id}`}
			/>
			{truncated ? (
				<Tooltip
					label={t(
						"pages.benchmarks.pairwise.truncatedHint",
						"One of the two answers was cut off by the token budget, so this verdict compared a fragment.",
					)}
					multiline={true}
					w={260}
				>
					<span>
						<StatusBadge
							color="orange"
							label={t("pages.benchmarks.pairwise.truncated", "truncated")}
							data-testid={`benchmark-pairwise-truncated-${comparison.id}`}
						/>
					</span>
				</Tooltip>
			) : null}
		</Group>
	);
}

/**
 * The pairwise reading of a project: the fitted score per run with its bootstrap interval, and the verdict matrix the
 * fit came from. They are ONE response and are rendered together on purpose — a score beside verdicts that did not
 * produce it is a number the reader cannot check.
 */
export function BenchmarkPairwiseMatrix({ projectId }: { projectId: string }) {
	const { t } = useTranslation();
	const query = useBenchmarkComparisons(projectId);
	const data = query.data;

	if (query.isLoading) {
		return (
			<Group gap="sm">
				<Loader size="sm" />
				<Text c="dimmed">{t("pages.benchmarks.pairwise.loading", "Loading pairwise verdicts…")}</Text>
			</Group>
		);
	}
	if (query.error) {
		return (
			<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="benchmark-pairwise-error">
				{apiErrorMessage(query.error, t("pages.benchmarks.pairwise.loadError", "Could not load the pairwise verdicts."))}
			</Alert>
		);
	}
	if (!data || data.items.length === 0) {
		return (
			<EmptyState
				message={t("pages.benchmarks.pairwise.empty", "No comparisons yet. They are queued when the project judges.")}
				size="sm"
			/>
		);
	}

	const pairs = groupComparisonsByPair(data.items);
	const fit = data.fit;

	return (
		<Stack gap="sm" data-testid="benchmark-pairwise-matrix">
			<Group gap="xs">
				<IconScale size={18} />
				<Text size="sm" fw={600}>
					{t("pages.benchmarks.pairwise.title", "Pairwise verdicts")}
				</Text>
				<Text size="xs" c="dimmed">
					{t("pages.benchmarks.pairwise.cohort", "gen {{generation}} · set v{{version}}", {
						generation: data.cohortGeneration,
						version: data.comparisonSetVersion,
					})}
				</Text>
			</Group>

			{/* A fit that no longer describes the cohort is not a score to render smaller — it is a score to withhold. */}
			{fit !== null && !fit.isCurrent ? (
				<Alert color="orange" icon={<IconAlertTriangle size={16} />} data-testid="benchmark-pairwise-stale">
					{t(
						"pages.benchmarks.pairwise.staleFit",
						"These scores were fitted from a different comparison set than the project now has. They refit on the next judging pass.",
					)}
				</Alert>
			) : null}

			{fit?.isCurrent ? (
				<Stack gap={4} data-testid="benchmark-pairwise-scores">
					<Text size="xs" c="dimmed">
						{t("pages.benchmarks.pairwise.fitSummary", "{{iterations}} iterations · {{replicates}} bootstrap replicates", {
							iterations: fit.iterations,
							replicates: fit.bootstrapReplicates,
						})}
					</Text>
					{fit.scores.map((score) => (
						<Group key={score.runId} gap="xs" wrap="nowrap">
							<Text size="xs" ff="monospace" w={80}>
								{shortId(score.runId)}
							</Text>
							<Text size="sm" fw={600} data-testid={`benchmark-pairwise-score-${score.runId}`}>
								{formatPairwiseScore(score) ?? t(`pages.benchmarks.rank.exclusion.${score.reason}`, score.reason ?? "—")}
							</Text>
							<Text size="xs" c="dimmed">
								{t("pages.benchmarks.pairwise.comparisonCount", "{{count}} comparisons", { count: score.comparisons })}
							</Text>
						</Group>
					))}
				</Stack>
			) : null}

			<ScrollArea.Autosize mah={320}>
				<Table striped={true} withTableBorder={true} data-testid="benchmark-pairwise-table">
					<Table.Thead>
						<Table.Tr>
							<Table.Th>{t("pages.benchmarks.pairwise.pair", "Pair")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.pairwise.orderA", "A shown first")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.pairwise.orderB", "B shown first")}</Table.Th>
						</Table.Tr>
					</Table.Thead>
					<Table.Tbody>
						{pairs.map((pair) => (
							<Table.Tr key={pair.key} data-testid={`benchmark-pairwise-pair-${pair.key}`}>
								<Table.Td>
									<Text size="xs" ff="monospace">
										{shortId(pair.runAId)} · {shortId(pair.runBId)}
									</Text>
								</Table.Td>
								{pair.orders.map((comparison, index) => (
									// biome-ignore lint/suspicious/noArrayIndexKey: the index IS the presentation order.
									<Table.Td key={`${pair.key}-${index}`}>
										<VerdictCell comparison={comparison} runAId={pair.runAId} />
									</Table.Td>
								))}
							</Table.Tr>
						))}
					</Table.Tbody>
				</Table>
			</ScrollArea.Autosize>
		</Stack>
	);
}
