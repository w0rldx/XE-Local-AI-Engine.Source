import { Group, Select, Stack, Text } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import type { BenchmarkCell } from "@/features/benchmarks/models/BenchmarkCells";
import { benchmarkCellLabel, benchmarkPairedDeltaFor, formatBenchmarkDelta } from "@/features/benchmarks/models/BenchmarkCells";
import { useBenchmarkCellComparison } from "@/features/benchmarks/queries/useBenchmarks";

interface BenchmarkPairedDeltaProps {
	projectId: string;
	/** The project's measured combinations, as the cell table lists them. */
	cells: readonly BenchmarkCell[];
}

/**
 * "A beats B by 6" is not a finding until it comes with an interval. This asks the node for the paired difference over
 * the items BOTH combinations answered rankably — resampling the per-item DIFFERENCES, which removes item difficulty
 * as a variance source and is what makes an interval tight enough to separate two models on a suite this small.
 *
 * The two readings it can produce are equally important. `separated` is the node's own flag and is rendered, never
 * re-derived from the bounds; and a pair the node reports NO entry for shares fewer than three items, which is
 * "this suite cannot answer that", not a delta of zero.
 */
export function BenchmarkPairedDelta({ projectId, cells }: BenchmarkPairedDeltaProps) {
	const { t } = useTranslation();
	const [aCellKey, setACellKey] = useState<string | null>(null);
	const [bCellKey, setBCellKey] = useState<string | null>(null);
	const selection = [aCellKey, bCellKey].filter((key): key is string => key !== null);
	const distinct = selection.length === 2 && aCellKey !== bCellKey;
	const comparison = useBenchmarkCellComparison(projectId, distinct ? selection : []);
	const autoLabel = t("pages.benchmarks.run.kvCacheTypeAuto", "Auto");
	const options = cells.map((cell) => ({ value: cell.cellKey, label: benchmarkCellLabel(cell, autoLabel) }));
	const delta =
		distinct && comparison.data ? benchmarkPairedDeltaFor(comparison.data.pairedDeltas, aCellKey as string, bCellKey as string) : null;

	return (
		<Stack gap="xs" data-testid="benchmark-paired-delta">
			<Text size="sm" fw={600}>
				{t("pages.benchmarks.paired.title", "Paired difference")}
			</Text>
			<Group grow={true} align="flex-end">
				<Select
					label={t("pages.benchmarks.paired.a", "A")}
					searchable={true}
					data={options}
					value={aCellKey}
					onChange={setACellKey}
					data-testid="benchmark-paired-a"
				/>
				<Select
					label={t("pages.benchmarks.paired.b", "B")}
					searchable={true}
					data={options}
					value={bCellKey}
					onChange={setBCellKey}
					data-testid="benchmark-paired-b"
				/>
			</Group>
			{!distinct ? (
				<Text size="xs" c="dimmed" data-testid="benchmark-paired-hint">
					{t("pages.benchmarks.paired.hint", "Pick two different combinations to compare them over the items they share.")}
				</Text>
			) : delta === null ? (
				// The node omits the entry rather than sending an interval three points cannot support.
				<Text size="sm" c="dimmed" data-testid="benchmark-paired-insufficient">
					{comparison.isLoading
						? t("pages.benchmarks.paired.loading", "Comparing…")
						: t(
								"pages.benchmarks.paired.insufficient",
								"These two share fewer than three rankably answered items, which cannot support an interval. That is a gap in the measurement, not a tie.",
							)}
				</Text>
			) : (
				<Stack gap={4}>
					<Group gap="sm" wrap="nowrap">
						<Text size="sm" fw={700} data-testid="benchmark-paired-value">
							{t("pages.benchmarks.paired.value", "A − B = {{delta}} [{{low}}, {{high}}]", {
								delta: formatBenchmarkDelta(delta.delta),
								low: formatBenchmarkDelta(delta.ciLow),
								high: formatBenchmarkDelta(delta.ciHigh),
							})}
						</Text>
						<StatusBadge
							color={delta.separated ? "green" : "gray"}
							label={
								delta.separated
									? t("pages.benchmarks.paired.separated", "separated")
									: t("pages.benchmarks.paired.notSeparated", "not separated")
							}
							data-testid="benchmark-paired-separated"
						/>
					</Group>
					<Text size="xs" c="dimmed" data-testid="benchmark-paired-detail">
						{t("pages.benchmarks.paired.detail", "Over the {{count}} scored items both answered, 95 % bootstrap interval.", {
							count: delta.sharedItemCount,
						})}
						{delta.separated
							? ""
							: ` ${t("pages.benchmarks.paired.notSeparatedHelp", "Zero is inside the interval: this suite does not separate them.")}`}
					</Text>
				</Stack>
			)}
		</Stack>
	);
}
