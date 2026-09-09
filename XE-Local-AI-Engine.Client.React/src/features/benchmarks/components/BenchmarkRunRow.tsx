import { ActionIcon, Checkbox, Group, Menu, Stack, Table, Text } from "@mantine/core";
import { IconDots, IconRefresh, IconRuler2, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { formatDurationSeconds, formatTimestamp } from "@/core/formatting/TimeFormatting";
import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { BenchmarkLaunchBadges } from "@/features/benchmarks/components/BenchmarkLaunchBadges";
import { FidelityCell } from "@/features/benchmarks/components/BenchmarkRunRow/FidelityCell";
import { QualityScoreCell } from "@/features/benchmarks/components/BenchmarkRunRow/QualityScoreCell";
import { RankCell } from "@/features/benchmarks/components/BenchmarkRunRow/RankCell";
import { ThroughputCell } from "@/features/benchmarks/components/BenchmarkRunRow/ThroughputCell";
import {
	BenchmarkIncompleteBadge,
	BenchmarkJudgeStateBadge,
	BenchmarkReasoningExhaustedBadge,
	BenchmarkStatusBadge,
	BenchmarkTruncatedBadge,
} from "@/features/benchmarks/components/BenchmarkStatusBadge";
import { canMeasureFidelity } from "@/features/benchmarks/models/BenchmarkFidelity";
import type { BenchmarkPairwiseRunScore, BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkQuantTag,
	isBenchmarkRunIncomplete,
	isBenchmarkRunReasoningExhausted,
	isBenchmarkRunTruncated,
	isRunTerminal,
} from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkRepeatStats } from "@/features/benchmarks/models/BenchmarkThroughput";

export interface BenchmarkRunRowProps {
	run: BenchmarkRunSummary;
	selected: boolean;
	isActionPending: boolean;
	/** Grouped child rows are indented under their model's row and carry no expander of their own. */
	nested?: boolean;
	expander?: React.ReactNode;
	modelLabel?: React.ReactNode;
	/** A group leader shows the BASE model; every other row shows the exact model name it ran. */
	modelName?: string;
	stats?: BenchmarkRepeatStats;
	pairwise?: BenchmarkPairwiseRunScore;
	onToggleRun: (runId: string) => void;
	onRejudgeRun: (run: BenchmarkRunSummary) => void;
	onMeasureFidelity: (run: BenchmarkRunSummary) => void;
	onDeleteRun: (run: BenchmarkRunSummary) => void;
}

export function BenchmarkRunRow({
	run,
	selected,
	isActionPending,
	nested = false,
	expander,
	modelLabel,
	modelName,
	stats,
	pairwise,
	onToggleRun,
	onRejudgeRun,
	onMeasureFidelity,
	onDeleteRun,
}: BenchmarkRunRowProps) {
	const { t } = useTranslation();
	const quant = benchmarkQuantTag(run.primaryModelName);
	return (
		<Table.Tr data-testid={`benchmark-run-row-${run.id}`}>
			<Table.Td>
				<Group gap={4} wrap="nowrap">
					{expander}
					<Checkbox
						checked={selected}
						aria-label={t("pages.benchmarks.run.select", "Show {{model}} in the detail view", {
							model: run.primaryModelName,
						})}
						onChange={() => onToggleRun(run.id)}
						data-testid={`benchmark-run-select-${run.id}`}
					/>
				</Group>
			</Table.Td>
			<Table.Td>
				<RankCell run={run} />
			</Table.Td>
			<Table.Td pl={nested ? "xl" : undefined}>
				<Stack gap={2} style={{ minWidth: 0 }}>
					<Text size="sm" fw={500} truncate="end">
						{modelName ?? run.primaryModelName}
					</Text>
					<Group gap={4} wrap="nowrap">
						{/* The quant is what tells a group's rows apart once the header carries the base model — without it a
						    grouped model's three quants would render as three identical-looking rows. */}
						{quant ? <StatusBadge color="gray" label={quant} data-testid={`benchmark-run-quant-${run.id}`} /> : null}
						{run.repeatIndex === null ? null : (
							<StatusBadge
								color={run.isWarmup ? "orange" : "gray"}
								label={
									run.isWarmup
										? t("pages.benchmarks.rank.warmupBadge", "warm-up")
										: t("pages.benchmarks.rank.repeatBadge", "#{{index}}", { index: run.repeatIndex })
								}
								data-testid={`benchmark-run-repeat-${run.id}`}
							/>
						)}
						<Text size="xs" c="dimmed" truncate="end">
							{modelLabel ??
								t(`pages.benchmarks.origin.${run.primaryModelOrigin ?? "legacy"}`, run.primaryModelOrigin ?? "Legacy / Unknown")}
						</Text>
					</Group>
				</Stack>
			</Table.Td>
			<Table.Td>
				<QualityScoreCell run={run} pairwise={pairwise} />
			</Table.Td>
			<Table.Td>
				<Text size="sm">{run.judge.score ?? "—"}</Text>
			</Table.Td>
			<Table.Td>
				<Text size="sm">{run.userScore ?? "—"}</Text>
			</Table.Td>
			<Table.Td>
				<ThroughputCell run={run} stats={stats} />
			</Table.Td>
			<Table.Td>
				<FidelityCell run={run} />
			</Table.Td>
			<Table.Td>
				<Text size="sm">{formatDurationSeconds(run.durationMs)}</Text>
			</Table.Td>
			<Table.Td>
				<Stack gap={2}>
					<BenchmarkLaunchBadges launch={run.primaryLaunch} data-testid={`benchmark-run-launch-${run.id}`} />
					<Text size="xs" c="dimmed">
						{t("pages.benchmarks.rank.context", "ctx {{tokens}}", {
							tokens: run.effectiveContextTokens ?? run.requestedContextTokens,
						})}
					</Text>
				</Stack>
			</Table.Td>
			<Table.Td>
				<Text size="xs">{formatTimestamp(run.createdAtUtc > 0 ? run.createdAtUtc : null)}</Text>
			</Table.Td>
			<Table.Td>
				<Group gap={4} wrap="nowrap">
					<BenchmarkStatusBadge status={run.primaryStatus} />
					{/* Reasoning exhaustion IS truncation, so it replaces the generic badge rather than adding a second
					    one — two badges saying "cut off" would not tell the operator which budget to raise. */}
					{isBenchmarkRunReasoningExhausted(run) ? (
						<BenchmarkReasoningExhaustedBadge testId={`benchmark-reasoning-exhausted-${run.id}`} />
					) : isBenchmarkRunTruncated(run) ? (
						<BenchmarkTruncatedBadge testId={`benchmark-truncated-${run.id}`} />
					) : null}
					{isBenchmarkRunIncomplete(run) ? <BenchmarkIncompleteBadge testId={`benchmark-incomplete-${run.id}`} /> : null}
					<BenchmarkJudgeStateBadge state={run.judge.state} />
				</Group>
			</Table.Td>
			<Table.Td>
				<Menu position="bottom-end" withinPortal={true}>
					<Menu.Target>
						<ActionIcon
							variant="subtle"
							aria-label={t("pages.benchmarks.run.actions", "Run actions")}
							data-testid={`benchmark-run-actions-${run.id}`}
						>
							<IconDots size={16} />
						</ActionIcon>
					</Menu.Target>
					<Menu.Dropdown>
						<Menu.Item
							leftSection={<IconRefresh size={14} />}
							disabled={isActionPending || run.primaryStatus !== "Succeeded"}
							onClick={() => onRejudgeRun(run)}
						>
							{t("pages.benchmarks.judge.rejudge", "Re-judge run")}
						</Menu.Item>
						{/* A re-measure inserts a new immutable attempt, so the previous numbers survive one that fails —
						    which is what makes this safe to offer without a confirmation. */}
						<Menu.Item
							leftSection={<IconRuler2 size={14} />}
							disabled={isActionPending || !canMeasureFidelity(run)}
							onClick={() => onMeasureFidelity(run)}
							data-testid={`benchmark-measure-fidelity-${run.id}`}
						>
							{t("pages.benchmarks.fidelity.measure", "Measure fidelity")}
						</Menu.Item>
						<Menu.Item
							color="red"
							leftSection={<IconTrash size={14} />}
							disabled={isActionPending || !isRunTerminal(run)}
							onClick={() => onDeleteRun(run)}
						>
							{t("pages.benchmarks.run.delete", "Delete terminal run")}
						</Menu.Item>
					</Menu.Dropdown>
				</Menu>
			</Table.Td>
		</Table.Tr>
	);
}
