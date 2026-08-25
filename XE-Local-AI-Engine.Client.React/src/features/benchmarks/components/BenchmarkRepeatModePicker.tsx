import { Group, NumberInput, Select } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { BenchmarkRepeatMode } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkAnswerVarianceTemperature, benchmarkRepeatModes, toBenchmarkRepeatMode } from "@/features/benchmarks/models/BenchmarkModels";

interface BenchmarkRepeatModePickerProps {
	mode: BenchmarkRepeatMode;
	/** Null while the operator has not overridden it: the node then applies its own default (0.7). */
	temperature: number | null;
	onChange: (mode: BenchmarkRepeatMode, temperature: number | null) => void;
}

/**
 * What a launch measures, on both entry points. Answer variance reveals its temperature because the two are one
 * decision: a sampled group with no temperature is not a different default, it is a different experiment. Shared
 * rather than written twice so the single-run control and the matrix can never offer different modes.
 */
export function BenchmarkRepeatModePicker({ mode, temperature, onChange }: BenchmarkRepeatModePickerProps) {
	const { t } = useTranslation();
	return (
		<Group grow={true} align="flex-start">
			<Select
				label={t("pages.benchmarks.run.repeatMode", "Repeat mode")}
				description={
					mode === "AnswerVariance"
						? t(
								"pages.benchmarks.run.repeatModeVarianceHelp",
								"Each repeat samples at the temperature below with its own seed, so the answers differ — the spread describes the model, not the machine.",
							)
						: t(
								"pages.benchmarks.run.repeatModeThroughputHelp",
								"Temperature 0 and one fixed seed: every repeat gives the identical answer, so the spread is the machine alone.",
							)
				}
				allowDeselect={false}
				value={mode}
				data={benchmarkRepeatModes.map((value) => ({
					value,
					label: t(`pages.benchmarks.run.repeatModes.${value}`, value),
				}))}
				onChange={(value) => onChange(toBenchmarkRepeatMode(value), temperature)}
				data-testid="benchmark-repeat-mode"
			/>
			{mode === "AnswerVariance" ? (
				<NumberInput
					label={t("pages.benchmarks.run.answerVarianceTemperature", "Temperature")}
					description={t("pages.benchmarks.run.answerVarianceTemperatureHelp", "Above 0, at most {{max}}. Seeds vary per repeat.", {
						max: benchmarkAnswerVarianceTemperature.max,
					})}
					min={0.1}
					max={benchmarkAnswerVarianceTemperature.max}
					step={0.1}
					decimalScale={2}
					clampBehavior="strict"
					value={temperature ?? benchmarkAnswerVarianceTemperature.default}
					onChange={(value) => onChange(mode, typeof value === "number" ? value : benchmarkAnswerVarianceTemperature.default)}
					data-testid="benchmark-answer-variance-temperature"
				/>
			) : null}
		</Group>
	);
}
