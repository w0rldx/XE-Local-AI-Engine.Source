import { Alert, Anchor, Button, Group, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { DatasetDriftAlert } from "@/features/training/components/DatasetDriftAlert";
import { type ComparisonReport, formatAccuracy, formatDelta } from "@/features/training/models/ComparisonModels";
import { useTrainingEvaluations } from "@/features/training/queries/useTrainingComparisons";

export function ComparisonReportCard({ report, onDelete }: { report: ComparisonReport; onDelete: () => void }) {
	const { t } = useTranslation();
	const deltas = report.deltas;
	// Both sides of a report score the SAME frozen hold-out set, so either evaluation answers "has the dataset moved
	// on since?". The lookup rides the run's evaluation list — with no training run recorded there is nothing to check.
	const evaluationsQuery = useTrainingEvaluations(report.trainingRunId);
	const evaluation =
		(evaluationsQuery.data ?? []).find(
			(row) => row.id === report.baseEvaluationRunId || row.id === report.tunedEvaluationRunId,
		) ?? null;

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
				{evaluation == null ? null : (
					<DatasetDriftAlert
						context="evaluation"
						datasetId={evaluation.datasetId}
						frozenFingerprint={evaluation.datasetContentFingerprint}
					/>
				)}

				{deltas == null || !deltas.accuracyAvailable ? (
					<Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
						{deltas?.unavailableReason ?? t("training.comparisons.report.unavailable", "The accuracy comparison is unavailable.")}
					</Alert>
				) : (
					<Stack gap="xs">
						<Text fw={500} size="sm">
							{t("training.comparisons.report.accuracy", "Hold-out accuracy")}
						</Text>
						<Table.ScrollContainer minWidth={520}>
							<Table data-testid="training-comparison-accuracy-table" striped={true} withTableBorder={true}>
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
						</Table.ScrollContainer>
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
						<Table.ScrollContainer minWidth={480}>
							<Table data-testid="training-comparison-benchmark-table" striped={true} withTableBorder={true}>
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
						</Table.ScrollContainer>
						{/*
						 * Carries both model names so the benchmarks page opens on the two runs this report is about.
						 * `Link` is used directly rather than through `Anchor component={Link}`: Mantine's polymorphic
						 * prop erases the router generics, which collapses `search` to an untyped reducer.
						 */}
						<Link search={{ base: deltas.baseModelName, tuned: deltas.tunedModelName }} to={nodeRoutePaths.benchmarks}>
							<Anchor component="span" size="xs">
								{t("training.comparisons.report.compareLive", "Compare outputs live on the benchmarks page")}
							</Anchor>
						</Link>
					</Stack>
				)}
			</Stack>
		</SectionCard>
	);
}

function formatBenchmarkValue(value: number | null): string {
	return value == null ? "—" : value.toFixed(value % 1 === 0 ? 0 : 2);
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
	return (
		<Table.Tr>
			<Table.Td>{label}</Table.Td>
			<Table.Td>{formatBenchmarkValue(base)}</Table.Td>
			<Table.Td>{formatBenchmarkValue(tuned)}</Table.Td>
			<Table.Td c={delta == null ? undefined : delta >= 0 ? "green" : "red"}>
				{delta == null ? "—" : `${delta >= 0 ? "+" : ""}${formatBenchmarkValue(delta)}`}
			</Table.Td>
		</Table.Tr>
	);
}
