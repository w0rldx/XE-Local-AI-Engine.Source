import { Alert, Anchor, Button, Group, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle, IconGitCompare, IconPlus } from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { toast } from "@/core/ui/notifications/Toast";
import { ComparisonCreateDialog } from "@/features/training/components/ComparisonCreateDialog";
import { type ComparisonReport, formatAccuracy, formatDelta } from "@/features/training/models/ComparisonModels";
import { useComparisonReports, useDeleteComparison } from "@/features/training/queries/useTrainingComparisons";

/**
 * Comparison reports: base versus tuned on the same frozen hold-out samples, per sample kind. Live side-by-side output
 * already exists on the benchmarks page, so this page links there rather than growing a second compare surface.
 */
export function ComparisonsPage() {
	const { t } = useTranslation();
	const [creating, setCreating] = useState(false);
	const reportsQuery = useComparisonReports();
	const deleteMutation = useDeleteComparison();
	const reports = reportsQuery.data ?? [];

	return (
		<PageShell>
			<PageHeader
				actions={
					<Button leftSection={<IconPlus size={16} />} onClick={() => setCreating(true)}>
						{t("training.comparisons.list.create", "New comparison")}
					</Button>
				}
				icon={<IconGitCompare size={24} />}
				subtitle={t(
					"pages.training.comparisons.subtitle",
					"Score a base model and its tuned counterpart on the same frozen hold-out samples, then read the difference.",
				)}
				title={t("pages.training.comparisons.title", "Comparisons")}
			/>

			<Stack gap="lg">
				{reports.length === 0 ? (
					<SectionCard title={t("training.comparisons.list.title", "Comparison reports")}>
						<EmptyState
							icon={<IconGitCompare size={28} opacity={0.5} />}
							message={t(
								"training.comparisons.list.empty",
								"No comparison reports yet. Create one from a finished training run.",
							)}
							size="sm"
						/>
					</SectionCard>
				) : (
					reports.map((report) => (
						<ComparisonReportCard
							key={report.id}
							onDelete={() =>
								deleteMutation.mutate(
									{ path: { comparisonId: report.id }, body: { expectedVersion: report.version } },
									{
										onError: (error) =>
											toast.error(
												apiErrorMessage(
													error,
													t("training.comparisons.list.deleteFailed", "Could not delete the comparison report."),
												),
											),
									},
								)
							}
							report={report}
						/>
					))
				)}
			</Stack>

			<ComparisonCreateDialog onClose={() => setCreating(false)} opened={creating} />
		</PageShell>
	);
}

function ComparisonReportCard({ report, onDelete }: { report: ComparisonReport; onDelete: () => void }) {
	const { t } = useTranslation();
	const deltas = report.deltas;

	return (
		<SectionCard
			actions={
				<Button color="red" onClick={onDelete} size="compact-sm" variant="subtle">
					{t("training.comparisons.list.delete", "Delete")}
				</Button>
			}
			title={report.name}
		>
			<Stack gap="md">
				{deltas == null || !deltas.accuracyAvailable ? (
					<Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
						{deltas?.unavailableReason ?? t("training.comparisons.report.unavailable", "The accuracy comparison is unavailable.")}
					</Alert>
				) : (
					<Stack gap="xs">
						<Text fw={500} size="sm">
							{t("training.comparisons.report.accuracy", "Hold-out accuracy")}
						</Text>
						<Table striped={true} withTableBorder={true}>
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("training.comparisons.report.kind", "Sample kind")}</Table.Th>
									<Table.Th>{t("training.comparisons.report.base", "Base")}</Table.Th>
									<Table.Th>{t("training.comparisons.report.tuned", "Tuned")}</Table.Th>
									<Table.Th>{t("training.comparisons.report.delta", "Delta")}</Table.Th>
								</Table.Tr>
							</Table.Thead>
							<Table.Tbody>
								{deltas.perKind.map((kind) => (
									<Table.Tr key={kind.kind}>
										<Table.Td>{kind.kind}</Table.Td>
										<Table.Td>{formatAccuracy(kind.baseAccuracy, kind.baseTotal)}</Table.Td>
										<Table.Td>{formatAccuracy(kind.tunedAccuracy, kind.tunedTotal)}</Table.Td>
										<Table.Td c={kind.accuracyDelta >= 0 ? "green" : "red"}>{formatDelta(kind.accuracyDelta)}</Table.Td>
									</Table.Tr>
								))}
								<Table.Tr>
									<Table.Td fw={600}>{t("training.comparisons.report.overall", "Overall")}</Table.Td>
									<Table.Td fw={600}>{formatAccuracy(deltas.baseAccuracy, deltas.baseScoredCount)}</Table.Td>
									<Table.Td fw={600}>{formatAccuracy(deltas.tunedAccuracy, deltas.tunedScoredCount)}</Table.Td>
									<Table.Td c={deltas.accuracyDelta >= 0 ? "green" : "red"} fw={600}>
										{formatDelta(deltas.accuracyDelta)}
									</Table.Td>
								</Table.Tr>
							</Table.Tbody>
						</Table>
						<Group gap="lg">
							<Text c="dimmed" size="xs">
								{`${deltas.baseModelName} · ${t("training.comparisons.report.samples", "{{passed}} of {{scored}} samples", {
									passed: deltas.basePassedCount,
									scored: deltas.baseScoredCount,
								})}`}
							</Text>
							<Text c="dimmed" size="xs">
								{`${deltas.tunedModelName} · ${t("training.comparisons.report.samples", "{{passed}} of {{scored}} samples", {
									passed: deltas.tunedPassedCount,
									scored: deltas.tunedScoredCount,
								})}`}
							</Text>
						</Group>
					</Stack>
				)}

				{deltas?.benchmark == null ? null : (
					<Stack gap="xs">
						<Text fw={500} size="sm">
							{t("training.comparisons.report.benchmark", "Benchmark deltas")}
						</Text>
						<Table striped={true} withTableBorder={true}>
							<Table.Tbody>
								<BenchmarkRow
									base={deltas.benchmark.baseTokensPerSecond}
									delta={deltas.benchmark.tokensPerSecondDelta}
									label={t("training.comparisons.report.tokensPerSecond", "Tokens per second")}
									tuned={deltas.benchmark.tunedTokensPerSecond}
								/>
								<BenchmarkRow
									base={deltas.benchmark.baseDurationMs}
									delta={null}
									label={t("training.comparisons.report.duration", "Duration (ms)")}
									tuned={deltas.benchmark.tunedDurationMs}
								/>
								<BenchmarkRow
									base={deltas.benchmark.baseUserScore}
									delta={deltas.benchmark.userScoreDelta}
									label={t("training.comparisons.report.userScore", "Your score")}
									tuned={deltas.benchmark.tunedUserScore}
								/>
								<BenchmarkRow
									base={deltas.benchmark.baseJudgeScore}
									delta={deltas.benchmark.judgeScoreDelta}
									label={t("training.comparisons.report.judgeScore", "Judge score")}
									tuned={deltas.benchmark.tunedJudgeScore}
								/>
							</Table.Tbody>
						</Table>
						<Anchor component={Link} size="xs" to={nodeRoutePaths.benchmarks}>
							{t("training.comparisons.report.compareLive", "Compare outputs live on the benchmarks page")}
						</Anchor>
					</Stack>
				)}
			</Stack>
		</SectionCard>
	);
}

function BenchmarkRow({
	label,
	base,
	tuned,
	delta,
}: {
	label: string;
	base: number | null;
	tuned: number | null;
	delta: number | null;
}) {
	const format = (value: number | null): string => (value == null ? "—" : value.toFixed(value % 1 === 0 ? 0 : 2));

	return (
		<Table.Tr>
			<Table.Td>{label}</Table.Td>
			<Table.Td>{format(base)}</Table.Td>
			<Table.Td>{format(tuned)}</Table.Td>
			<Table.Td c={delta == null ? undefined : delta >= 0 ? "green" : "red"}>
				{delta == null ? "—" : `${delta >= 0 ? "+" : ""}${format(delta)}`}
			</Table.Td>
		</Table.Tr>
	);
}
