import { Group, Text, UnstyledButton } from "@mantine/core";
import { IconStar, IconStarFilled } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

interface BenchmarkScorePickerProps {
	value: number | null;
	disabled: boolean;
	isSaving?: boolean;
	onChange: (score: number) => void;
}

export function BenchmarkScorePicker({ value, disabled, isSaving = false, onChange }: BenchmarkScorePickerProps) {
	const { t } = useTranslation();
	return (
		<Group gap="xs" role="group" aria-label={t("pages.benchmarks.score.label", "Operator score")}>
			<Text size="sm" fw={600}>
				{t("pages.benchmarks.score.label", "Operator score")}
			</Text>
			{[1, 2, 3, 4, 5].map((score) => {
				const selected = value === score;
				const label = t("pages.benchmarks.score.value", "Score {{score}} of 5", { score });
				return (
					<UnstyledButton
						key={score}
						disabled={disabled || isSaving}
						onClick={() => onChange(score)}
						aria-label={label}
						aria-pressed={selected}
						data-testid={`benchmark-score-${score}`}
					>
						{selected ? <IconStarFilled size={20} aria-hidden={true} /> : <IconStar size={20} aria-hidden={true} />}
					</UnstyledButton>
				);
			})}
		</Group>
	);
}
