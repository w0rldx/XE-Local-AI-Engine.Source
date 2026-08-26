import { Checkbox, Group, Stack, Table, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import type { BenchmarkCell } from "@/features/benchmarks/models/BenchmarkCells";
import { missingBenchmarkCellItems } from "@/features/benchmarks/models/BenchmarkCells";
import type { BenchmarkRunVerifier } from "@/features/benchmarks/models/BenchmarkModels";
import { rankExclusionAction } from "@/features/benchmarks/models/BenchmarkRanking";
import type { BenchmarkTaskItem } from "@/features/benchmarks/models/BenchmarkTaskItems";
import { benchmarkNiahCaseLabel, scorableBenchmarkTaskItems } from "@/features/benchmarks/models/BenchmarkTaskItems";
import { useBenchmarkRunDetails } from "@/features/benchmarks/queries/useBenchmarks";

interface BenchmarkItemBreakdownProps {
	cell: BenchmarkCell;
	/** Every task item of the project, so a row can name the question its run answered. */
	items: readonly BenchmarkTaskItem[];
	selectedRunIds: readonly string[];
	onToggleRun: (runId: string) => void;
}

function VerifierEvidence({ verifiers, runId }: { verifiers: readonly BenchmarkRunVerifier[]; runId: string }) {
	const { t } = useTranslation();
	if (verifiers.length === 0) {
		return null;
	}
	return (
		<Stack gap={2} data-testid={`benchmark-cell-verifiers-${runId}`}>
			{verifiers.map((verifier) => (
				<Group key={verifier.id} gap={6} wrap="nowrap" align="flex-start">
					<StatusBadge
						color={verifier.passed ? "green" : "red"}
						label={t(`pages.benchmarks.verifier.kinds.${verifier.kind}`, verifier.kind)}
						data-testid={`benchmark-cell-verifier-${runId}-${verifier.id}`}
					/>
					<Text size="xs" c="dimmed">
						{verifier.detail}
					</Text>
				</Group>
			))}
		</Stack>
	);
}

/**
 * One cell, item by item: which question each run answered, what it scored, and — for a deterministic criterion — the
 * node's own sentence about what it checked. This is where `item-incomplete` stops being a label and names the
 * question that was never answered.
 *
 * The run DETAILS are read only for the cell the operator opened, through the same per-run cache the live panes and
 * the compare view use, so opening one cell costs nothing that is already on screen.
 */
export function BenchmarkItemBreakdown({ cell, items, selectedRunIds, onToggleRun }: BenchmarkItemBreakdownProps) {
	const { t } = useTranslation();
	const { runs } = useBenchmarkRunDetails(cell.items.map((item) => item.runId));
	const details = new Map(runs.map((run) => [run.id, run]));
	const itemsById = new Map(items.map((item) => [item.id, item]));
	const missing = missingBenchmarkCellItems(cell, scorableBenchmarkTaskItems(items));

	const label = (taskItemId: string | null, index: number | null): string => {
		const item = taskItemId === null ? undefined : itemsById.get(taskItemId);
		if (item === undefined) {
			// A pre-suite run names no item, and the node ranks it on its own run. Saying "item 1" would invent one.
			return taskItemId === null
				? t("pages.benchmarks.cells.legacyItem", "the project's task")
				: t("pages.benchmarks.cells.unknownItem", "item {{index}}", { index: (index ?? 0) + 1 });
		}
		return benchmarkNiahCaseLabel(item) ?? item.prompt;
	};

	return (
		<Stack gap="xs" data-testid={`benchmark-cell-breakdown-${cell.cellKey}`}>
			<Table verticalSpacing="xs">
				<Table.Tbody>
					{cell.items.map((answer) => {
						const item = answer.taskItemId === null ? undefined : itemsById.get(answer.taskItemId);
						const detail = details.get(answer.runId);
						return (
							<Table.Tr key={answer.runId} data-testid={`benchmark-cell-item-${answer.runId}`}>
								<Table.Td width={40}>
									<Checkbox
										checked={selectedRunIds.includes(answer.runId)}
										aria-label={t("pages.benchmarks.cells.selectRun", "Show this item's run in the detail view")}
										onChange={() => onToggleRun(answer.runId)}
										data-testid={`benchmark-cell-item-select-${answer.runId}`}
									/>
								</Table.Td>
								<Table.Td>
									<Stack gap={2} style={{ minWidth: 0 }}>
										<Text size="sm" truncate="end">
											{label(answer.taskItemId, answer.taskItemIndex)}
										</Text>
										<Group gap={4} wrap="nowrap">
											{item?.countsTowardScore === false ? (
												<StatusBadge
													color="gray"
													label={t("pages.benchmarks.items.notScored", "own axis")}
													data-testid={`benchmark-cell-item-unscored-${answer.runId}`}
												/>
											) : null}
											{answer.rankExclusionReason === null ? null : (
												<StatusBadge
													color="orange"
													label={t(
														`pages.benchmarks.rank.exclusionShort.${answer.rankExclusionReason}`,
														answer.rankExclusionReason,
													)}
													data-testid={`benchmark-cell-item-exclusion-${answer.runId}`}
												/>
											)}
											{answer.primaryStopReason === null || answer.primaryStopReason === "stop" ? null : (
												<Text size="xs" c="dimmed">
													{answer.primaryStopReason}
												</Text>
											)}
										</Group>
										{detail === undefined ? null : <VerifierEvidence verifiers={detail.judge.verifiers} runId={answer.runId} />}
									</Stack>
								</Table.Td>
								<Table.Td width={80} align="right">
									<Text size="sm" fw={700} data-testid={`benchmark-cell-item-score-${answer.runId}`}>
										{answer.qualityScore ?? "—"}
									</Text>
								</Table.Td>
							</Table.Tr>
						);
					})}
				</Table.Tbody>
			</Table>
			{missing.length === 0 ? null : (
				<Text size="xs" c="orange" data-testid={`benchmark-cell-missing-${cell.cellKey}`}>
					{t("pages.benchmarks.cells.missing", "Never answered: {{items}}", {
						items: missing.map((item) => item.prompt.slice(0, 40)).join(" · "),
					})}
				</Text>
			)}
			{cell.rankExclusionReason === null ? null : (
				<Text size="xs" c="dimmed" data-testid={`benchmark-cell-action-${cell.cellKey}`}>
					{t(`pages.benchmarks.rank.action.${rankExclusionAction(cell.rankExclusionReason)}`, "")}
				</Text>
			)}
		</Stack>
	);
}
