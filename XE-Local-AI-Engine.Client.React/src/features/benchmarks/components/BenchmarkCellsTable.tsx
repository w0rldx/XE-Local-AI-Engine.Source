import { ActionIcon, Button, Group, Stack, Table, Text, Tooltip } from "@mantine/core";
import { IconChevronDown, IconChevronRight, IconRocket } from "@tabler/icons-react";
import { Fragment, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { BenchmarkItemBreakdown } from "@/features/benchmarks/components/BenchmarkItemBreakdown";
import type { BenchmarkCell } from "@/features/benchmarks/models/BenchmarkCells";
import { benchmarkNiahRecall, missingBenchmarkCellItems, sortBenchmarkCells } from "@/features/benchmarks/models/BenchmarkCells";
import type { BenchmarkRankCohort } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkBaseModelLabel, benchmarkQuantTag } from "@/features/benchmarks/models/BenchmarkModels";
import { rankExclusionAction } from "@/features/benchmarks/models/BenchmarkRanking";
import type { BenchmarkTaskItem } from "@/features/benchmarks/models/BenchmarkTaskItems";
import { scorableBenchmarkTaskItems } from "@/features/benchmarks/models/BenchmarkTaskItems";

interface BenchmarkCellsTableProps {
	cells: readonly BenchmarkCell[];
	cohort: BenchmarkRankCohort;
	/** How many leaf items count toward the score right now. A cell holding fewer is why a reader sees `item-incomplete`. */
	scorableItemCount: number;
	items: readonly BenchmarkTaskItem[];
	selectedRunIds: readonly string[];
	isActionPending?: boolean;
	onToggleRun: (runId: string) => void;
	/**
	 * Re-measure this combination. The node has no per-item start — a freeze always fans out over every leaf item — so
	 * the smallest thing that can answer a missing or revised item is the whole cell, and the button says so.
	 */
	onRerunCell: (cell: BenchmarkCell) => void;
}

/**
 * The ranked table of a multi-item project. A CELL is one model at one KV type over one repeat group, holding one run
 * per task item; its score is the mean over the items that count and it ranks only when every one of them produced a
 * rankable score — so a cell is either whole or it is out, never a partial mean quietly compared against a full one.
 *
 * Long-context probes are reported on their own recall axis and are deliberately not in that mean: recall at 32k and
 * answer quality are different measurements, and their average is neither.
 */
export function BenchmarkCellsTable({
	cells,
	cohort,
	scorableItemCount,
	items,
	selectedRunIds,
	isActionPending = false,
	onToggleRun,
	onRerunCell,
}: BenchmarkCellsTableProps) {
	const { t } = useTranslation();
	const [expanded, setExpanded] = useState<string[]>([]);
	const ordered = useMemo(() => sortBenchmarkCells(cells), [cells]);
	const scorable = useMemo(() => scorableBenchmarkTaskItems(items), [items]);
	// A project whose every leaf is on its own axis — a pure long-context probe — has nothing to take a mean OF, so the
	// node excludes its cells as `no-score`. The ordinary reading of that reason ("set an operator score") is wrong
	// here: no score an operator could give would enter a mean that does not exist. The recall column IS the reading.
	const displayOnly = scorableItemCount === 0;

	if (cells.length === 0) {
		return <EmptyState message={t("pages.benchmarks.cells.empty", "No measured combinations yet.")} size="sm" />;
	}

	return (
		<Stack gap="sm" data-testid="benchmark-cells">
			{displayOnly ? (
				<Text size="sm" c="dimmed" data-testid="benchmark-cells-display-only">
					{t(
						"pages.benchmarks.cells.displayOnly",
						"No task item of this project counts toward a score, so no combination is ranked. Recall is the measurement here.",
					)}
				</Text>
			) : null}
			<Text size="sm" c="dimmed" data-testid="benchmark-cells-cohort">
				{t("pages.benchmarks.cells.cohort", "{{ranked}} of {{scored}} combinations ranked, each over {{items}} scored items", {
					ranked: cohort.rankedCount,
					scored: cohort.totalScored,
					items: scorableItemCount,
				})}
			</Text>
			<Table.ScrollContainer minWidth={760}>
				<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="benchmark-cells-table">
					<Table.Thead>
						<Table.Tr>
							<Table.Th />
							<Table.Th>{t("pages.benchmarks.rank.rank", "Rank")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.model", "Model")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.cells.quality", "Suite mean")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.cells.items", "Items")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.cells.recall", "Recall")}</Table.Th>
							<Table.Th>{t("pages.benchmarks.rank.actions", "Actions")}</Table.Th>
						</Table.Tr>
					</Table.Thead>
					<Table.Tbody>
						{ordered.map((cell) => {
							const isOpen = expanded.includes(cell.cellKey);
							const quant = benchmarkQuantTag(cell.primaryModelName);
							const recall = benchmarkNiahRecall(cell, items);
							const answered = scorable.length - missingBenchmarkCellItems(cell, scorable).length;
							return (
								<Fragment key={cell.cellKey}>
									<Table.Tr data-testid={`benchmark-cell-row-${cell.cellKey}`}>
										<Table.Td width={40}>
											<ActionIcon
												variant="subtle"
												size="sm"
												aria-label={t("pages.benchmarks.cells.expand", "Show this combination item by item")}
												aria-expanded={isOpen}
												onClick={() =>
													setExpanded((current) =>
														current.includes(cell.cellKey)
															? current.filter((key) => key !== cell.cellKey)
															: [...current, cell.cellKey],
													)
												}
												data-testid={`benchmark-cell-toggle-${cell.cellKey}`}
											>
												{isOpen ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
											</ActionIcon>
										</Table.Td>
										<Table.Td>
											{cell.rank === null ? (
												<Tooltip
													label={
														// On a display-only project the reason is real but its ACTION is not, so the
														// sentence stops at what is true: there is nothing to rank.
														displayOnly
															? t(
																	"pages.benchmarks.cells.displayOnlyCell",
																	"Nothing here counts toward a score, so there is nothing to rank.",
																)
															: cell.rankExclusionReason === null
																? t("pages.benchmarks.cells.unranked", "Not ranked.")
																: `${t(`pages.benchmarks.rank.exclusion.${cell.rankExclusionReason}`, cell.rankExclusionReason)} — ${t(
																		`pages.benchmarks.rank.action.${rankExclusionAction(cell.rankExclusionReason)}`,
																		"",
																	)}`
													}
													multiline={true}
													w={280}
												>
													<Group gap={6} wrap="nowrap">
														<Text size="sm">—</Text>
														{cell.rankExclusionReason === null || displayOnly ? null : (
															<StatusBadge
																color="orange"
																label={t(
																	`pages.benchmarks.rank.exclusionShort.${cell.rankExclusionReason}`,
																	cell.rankExclusionReason,
																)}
																data-testid={`benchmark-cell-exclusion-${cell.cellKey}`}
															/>
														)}
													</Group>
												</Tooltip>
											) : (
												<Text size="sm" fw={700}>
													{cell.rank}
												</Text>
											)}
										</Table.Td>
										<Table.Td>
											<Stack gap={2} style={{ minWidth: 0 }}>
												<Text size="sm" fw={500} truncate="end">
													{benchmarkBaseModelLabel(cell.primaryModelName)}
												</Text>
												<Group gap={4} wrap="nowrap">
													{quant ? <StatusBadge color="gray" label={quant} /> : null}
													<StatusBadge
														color="gray"
														label={cell.kvCacheType ?? t("pages.benchmarks.run.kvCacheTypeAuto", "Auto")}
														data-testid={`benchmark-cell-kv-${cell.cellKey}`}
													/>
													{cell.repeatIndex === null ? null : (
														<StatusBadge
															color="gray"
															label={t("pages.benchmarks.rank.repeatBadge", "#{{index}}", { index: cell.repeatIndex })}
														/>
													)}
												</Group>
											</Stack>
										</Table.Td>
										<Table.Td>
											<Text size="sm" fw={700} data-testid={`benchmark-cell-quality-${cell.cellKey}`}>
												{cell.quality ?? "—"}
											</Text>
										</Table.Td>
										<Table.Td>
											<Text size="sm" data-testid={`benchmark-cell-items-${cell.cellKey}`}>
												{t("pages.benchmarks.cells.itemsOf", "{{answered}} of {{total}}", {
													answered,
													total: scorable.length,
												})}
											</Text>
										</Table.Td>
										<Table.Td>
											{/* Reported, never averaged in: the mean beside it is a mean of answer quality. */}
											<Text size="sm" c="dimmed" data-testid={`benchmark-cell-recall-${cell.cellKey}`}>
												{recall.recall === null
													? "—"
													: t("pages.benchmarks.cells.recallValue", "{{found}} of {{graded}} needles", {
															found: recall.found,
															graded: recall.graded,
														})}
											</Text>
										</Table.Td>
										<Table.Td>
											<Button
												variant="subtle"
												size="xs"
												leftSection={<IconRocket size={14} />}
												disabled={isActionPending}
												onClick={() => onRerunCell(cell)}
												data-testid={`benchmark-cell-rerun-${cell.cellKey}`}
											>
												{t("pages.benchmarks.cells.rerun", "Re-run")}
											</Button>
										</Table.Td>
									</Table.Tr>
									{isOpen ? (
										<Table.Tr>
											<Table.Td colSpan={7}>
												<BenchmarkItemBreakdown
													cell={cell}
													items={items}
													selectedRunIds={selectedRunIds}
													onToggleRun={onToggleRun}
												/>
											</Table.Td>
										</Table.Tr>
									) : null}
								</Fragment>
							);
						})}
					</Table.Tbody>
				</Table>
			</Table.ScrollContainer>
		</Stack>
	);
}
