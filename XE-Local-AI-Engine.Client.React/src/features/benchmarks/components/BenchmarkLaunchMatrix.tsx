import { Alert, Button, Checkbox, Group, NumberInput, ScrollArea, Stack, Switch, Text } from "@mantine/core";
import { IconAlertTriangle, IconRocket } from "@tabler/icons-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { BenchmarkRepeatModePicker } from "@/features/benchmarks/components/BenchmarkRepeatModePicker";
import type {
	BenchmarkEligibleModel,
	BenchmarkKvCacheType,
	BenchmarkRepeatMode,
} from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkBaseModelLabel, benchmarkKvCacheTypes, benchmarkQuantTag } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkRunEstimate, formatBenchmarkDuration } from "@/features/benchmarks/models/BenchmarkRunEstimate";
import { benchmarkTaskItemLimits } from "@/features/benchmarks/models/BenchmarkTaskItems";
import type { BenchmarkBatchRejection } from "@/features/benchmarks/queries/useBenchmarks";

/** The picker's "Auto" entry is a UI-only value; it is sent as an omitted `kvCacheType`, which the node resolves. */
const autoKvCacheType = "auto";
type KvChoice = BenchmarkKvCacheType | typeof autoKvCacheType;
const kvChoices: KvChoice[] = [autoKvCacheType, ...benchmarkKvCacheTypes];

// Mirrors the node's BenchmarkRunFreezeService.MaxRepeatCount, so the dialog refuses what the node would.
const repeatCountLimits = { min: 1, max: 10 } as const;

export interface BenchmarkMatrixSelection {
	items: { modelName: string; kvCacheType?: string }[];
	repeatCount: number;
	warmup: boolean;
	repeatMode: BenchmarkRepeatMode;
	/** Null = the node's own default; omitted entirely in throughput mode, which is deterministic by definition. */
	answerVarianceTemperature: number | null;
}

interface BenchmarkLaunchMatrixProps {
	models: readonly BenchmarkEligibleModel[];
	/** The project's leaf task items. Every combination runs each of them, so this multiplies the whole matrix. */
	leafItemCount: number;
	/** The project's median completed run, for the time estimate. Null when it has none to extrapolate from. */
	medianRunMs: number | null;
	/** The cells the node refused on the last submit, shown in place rather than as a toast that scrolls away. */
	rejected: readonly BenchmarkBatchRejection[];
	isSubmitting: boolean;
	onSubmit: (selection: BenchmarkMatrixSelection) => void;
	onCancel: () => void;
}

/**
 * The launch matrix: pick several models and several KV-cache types, and every combination is enqueued as its own run
 * group. It exists because the single-run control makes an operator start twelve runs by hand to answer one question
 * ("which quant of this model, at which KV type, on this box"), and each of those twelve is a separate chance to pick
 * the wrong project version.
 *
 * The submit is ONE request, and the node answers per cell — so a model that turns out to be ineligible is reported
 * here beside the others rather than failing the whole matrix.
 */
export function BenchmarkLaunchMatrix({
	models,
	leafItemCount,
	medianRunMs,
	rejected,
	isSubmitting,
	onSubmit,
	onCancel,
}: BenchmarkLaunchMatrixProps) {
	const { t } = useTranslation();
	const [selectedModels, setSelectedModels] = useState<string[]>([]);
	const [selectedKvTypes, setSelectedKvTypes] = useState<string[]>([autoKvCacheType]);
	const [repeatCount, setRepeatCount] = useState<number>(repeatCountLimits.min);
	const [warmup, setWarmup] = useState(false);
	const [repeatMode, setRepeatMode] = useState<BenchmarkRepeatMode>("Throughput");
	const [answerVarianceTemperature, setAnswerVarianceTemperature] = useState<number | null>(null);

	const cellCount = selectedModels.length * selectedKvTypes.length;
	const estimate = benchmarkRunEstimate({ cellCount, leafItemCount, repeatCount, warmup }, medianRunMs);
	const totalRuns = estimate.totalRuns;
	// The node refuses the whole freeze above its per-request cap, so the dialog refuses it here rather than sending a
	// request that comes back naming a number the operator never saw computed.
	const canSubmit = cellCount > 0 && !estimate.exceedsCap && !isSubmitting;

	// Sorted by base model so a model's quants sit together in the picker, which is the comparison the matrix is for.
	const options = useMemo(
		() =>
			[...models].sort(
				(left, right) =>
					benchmarkBaseModelLabel(left.modelName).localeCompare(benchmarkBaseModelLabel(right.modelName)) ||
					left.modelName.localeCompare(right.modelName),
			),
		[models],
	);

	const submit = (): void => {
		onSubmit({
			items: selectedModels.flatMap((modelName) =>
				selectedKvTypes.map((kvCacheType) =>
					// Omitted rather than sent as null: an absent type is Auto, which the node resolves at freeze.
					kvCacheType === autoKvCacheType ? { modelName } : { modelName, kvCacheType },
				),
			),
			repeatCount,
			warmup,
			repeatMode,
			answerVarianceTemperature: repeatMode === "AnswerVariance" ? answerVarianceTemperature : null,
		});
	};

	return (
		<Stack gap="md" data-testid="benchmark-launch-matrix">
			<Checkbox.Group
				value={selectedModels}
				onChange={setSelectedModels}
				label={t("pages.benchmarks.matrix.models", "Models")}
				description={t("pages.benchmarks.matrix.modelsHelp", "Every selected model runs against every selected KV type.")}
			>
				<ScrollArea.Autosize mah={260} mt="xs">
					<Stack gap={6}>
						{options.map((model) => {
							const quant = benchmarkQuantTag(model.modelName);
							return (
								<Checkbox
									key={model.modelName}
									value={model.modelName}
									data-testid={`benchmark-matrix-model-${model.modelName}`}
									label={
										<Group gap={6} wrap="nowrap">
											<Text size="sm">{benchmarkBaseModelLabel(model.modelName)}</Text>
											{quant ? <StatusBadge color="gray" label={quant} /> : null}
										</Group>
									}
								/>
							);
						})}
					</Stack>
				</ScrollArea.Autosize>
			</Checkbox.Group>

			<Checkbox.Group
				value={selectedKvTypes}
				onChange={setSelectedKvTypes}
				label={t("pages.benchmarks.run.kvCacheType", "KV cache type")}
				description={t(
					"pages.benchmarks.run.kvCacheTypeHelp",
					"Quantized types launch with flash attention on. Auto uses q8_0 on GPU when the selected binary supports it, otherwise f16.",
				)}
			>
				<Group gap="md" mt="xs">
					{kvChoices.map((type) => (
						<Checkbox
							key={type}
							value={type}
							label={type === autoKvCacheType ? t("pages.benchmarks.run.kvCacheTypeAuto", "Auto") : type}
							data-testid={`benchmark-matrix-kv-${type}`}
						/>
					))}
				</Group>
			</Checkbox.Group>

			<BenchmarkRepeatModePicker
				mode={repeatMode}
				temperature={answerVarianceTemperature}
				onChange={(mode, temperature) => {
					setRepeatMode(mode);
					setAnswerVarianceTemperature(temperature);
				}}
			/>

			<Group grow={true} align="flex-start">
				<NumberInput
					label={t("pages.benchmarks.matrix.repeatCount", "Repeats per combination")}
					description={
						repeatMode === "AnswerVariance"
							? t(
									"pages.benchmarks.matrix.repeatCountVarianceHelp",
									"Each repeat samples its own answer, so the repeats measure how much the ANSWER moves. Each repeat is still its own model load.",
								)
							: t(
									"pages.benchmarks.matrix.repeatCountHelp",
									"Sampling is deterministic, so repeats do not change the answer — they measure how much the speed moves between launches. Each repeat is its own model load.",
								)
					}
					min={repeatCountLimits.min}
					max={repeatCountLimits.max}
					clampBehavior="strict"
					allowDecimal={false}
					value={repeatCount}
					onChange={(value) => setRepeatCount(typeof value === "number" ? value : repeatCountLimits.min)}
					data-testid="benchmark-matrix-repeat-count"
				/>
				<Switch
					mt="xl"
					checked={warmup}
					onChange={(event) => setWarmup(event.currentTarget.checked)}
					label={t("pages.benchmarks.matrix.warmup", "Warm-up run first")}
					description={t(
						"pages.benchmarks.matrix.warmupHelp",
						"Adds one extra run per combination that is never ranked and never counted in the spread.",
					)}
					data-testid="benchmark-matrix-warmup"
				/>
			</Group>

			<Stack gap={2}>
				<Text size="sm" c="dimmed" data-testid="benchmark-matrix-summary">
					{t(
						"pages.benchmarks.matrix.summary",
						"{{cells}} combinations × {{items}} task items × {{perCell}} runs = {{total}} runs",
						{ cells: cellCount, items: leafItemCount, perCell: estimate.runsPerItem, total: totalRuns },
					)}
					{estimate.estimatedMs === null
						? ""
						: ` · ${t("pages.benchmarks.matrix.estimate", "about {{duration}}", {
								duration: formatBenchmarkDuration(estimate.estimatedMs),
							})}`}
				</Text>
				{estimate.exceedsCap ? (
					<Text size="sm" c="red" data-testid="benchmark-matrix-over-cap">
						{t(
							"pages.benchmarks.matrix.overCap",
							"The node refuses more than {{max}} runs in one request. Pick fewer combinations, fewer repeats, or split the matrix.",
							{ max: benchmarkTaskItemLimits.maxRunsPerRequest },
						)}
					</Text>
				) : null}
			</Stack>

			{rejected.length > 0 ? (
				<Alert color="orange" icon={<IconAlertTriangle size={16} />} data-testid="benchmark-matrix-rejected">
					<Stack gap={2}>
						<Text size="sm" fw={600}>
							{t("pages.benchmarks.matrix.rejected", "{{count}} combinations were not started", { count: rejected.length })}
						</Text>
						{rejected.map((item) => (
							<Text key={`${item.modelName}-${item.kvCacheType ?? autoKvCacheType}`} size="xs">
								{`${item.modelName} · ${item.kvCacheType ?? autoKvCacheType} — ${item.message}`}
							</Text>
						))}
					</Stack>
				</Alert>
			) : null}

			<Group justify="flex-end">
				<Button variant="default" onClick={onCancel}>
					{t("common.cancel", "Cancel")}
				</Button>
				<Button
					leftSection={<IconRocket size={16} />}
					disabled={!canSubmit}
					loading={isSubmitting}
					onClick={submit}
					data-testid="benchmark-matrix-start"
				>
					{t("pages.benchmarks.matrix.start", "Start {{count}} runs", { count: totalRuns })}
				</Button>
			</Group>
		</Stack>
	);
}
