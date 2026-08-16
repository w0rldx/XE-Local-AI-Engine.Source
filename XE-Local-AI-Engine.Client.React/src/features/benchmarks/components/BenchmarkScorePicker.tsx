import { Button, Group, NumberInput, Slider, Stack, Text } from "@mantine/core";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

interface BenchmarkScorePickerProps {
	/** The stored override, or null when the run has none. */
	value: number | null;
	disabled: boolean;
	isSaving?: boolean;
	onChange: (score: number) => void;
	onClear: () => void;
}

const minScore = 0;
const maxScore = 100;
const presets = [0, 25, 50, 75, 100] as const;

const clamp = (value: number): number => Math.min(maxScore, Math.max(minScore, Math.round(value)));

/**
 * The operator's 0..100 override. Nothing is written until the operator commits: a Mantine `NumberInput`/`Slider`
 * fires a spurious `onChange` on mount, and an auto-saving control would turn that into a real score on a run that
 * deliberately had none. The presets commit on click because a click IS the commit.
 */
export function BenchmarkScorePicker({ value, disabled, isSaving = false, onChange, onClear }: BenchmarkScorePickerProps) {
	const { t } = useTranslation();
	const [draft, setDraft] = useState(value ?? 0);
	useEffect(() => setDraft(value ?? 0), [value]);
	const locked = disabled || isSaving;
	const label = t("pages.benchmarks.score.label", "Operator score");
	const commit = (score: number): void => {
		const bounded = clamp(score);
		setDraft(bounded);
		onChange(bounded);
	};

	return (
		<Stack gap="xs" role="group" aria-label={label}>
			<Group justify="space-between" align="center">
				<Text size="sm" fw={600}>
					{label}
				</Text>
				<Text size="xs" c="dimmed">
					{value === null
						? t("pages.benchmarks.score.unset", "No override — the judge score is used")
						: t("pages.benchmarks.score.current", "Override: {{score}} / 100", { score: value })}
				</Text>
			</Group>
			<Group gap="sm" align="flex-end" wrap="nowrap">
				<NumberInput
					aria-label={t("pages.benchmarks.score.input", "Operator score, 0 to 100")}
					min={minScore}
					max={maxScore}
					step={5}
					clampBehavior="strict"
					disabled={locked}
					value={draft}
					w={110}
					onChange={(next) => setDraft(clamp(typeof next === "number" ? next : Number(next) || 0))}
					data-testid="benchmark-score-input"
				/>
				<Slider
					flex={1}
					min={minScore}
					max={maxScore}
					step={1}
					disabled={locked}
					value={draft}
					label={draft}
					onChange={setDraft}
					aria-label={t("pages.benchmarks.score.slider", "Operator score slider")}
					data-testid="benchmark-score-slider"
				/>
			</Group>
			<Group gap="xs">
				{presets.map((preset) => (
					<Button
						key={preset}
						size="compact-xs"
						variant={value === preset ? "filled" : "default"}
						disabled={locked}
						aria-pressed={value === preset}
						onClick={() => commit(preset)}
						data-testid={`benchmark-score-preset-${preset}`}
					>
						{preset}
					</Button>
				))}
				<Button
					size="compact-xs"
					disabled={locked || draft === value}
					loading={isSaving}
					onClick={() => commit(draft)}
					data-testid="benchmark-score-save"
				>
					{t("pages.benchmarks.score.save", "Save score")}
				</Button>
				<Button
					size="compact-xs"
					variant="subtle"
					color="red"
					disabled={disabled || isSaving || value === null}
					onClick={onClear}
					data-testid="benchmark-score-clear"
				>
					{t("pages.benchmarks.score.clear", "Clear override")}
				</Button>
			</Group>
		</Stack>
	);
}
