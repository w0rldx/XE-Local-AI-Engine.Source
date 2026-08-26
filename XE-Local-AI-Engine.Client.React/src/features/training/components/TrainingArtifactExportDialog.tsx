import { Button, Select, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import {
	defaultTrainingExportQuantization,
	trainingExportQuantizations,
	type TrainingArtifactKindValue,
} from "@/features/training/models/TrainingModels";

interface TrainingArtifactExportDialogProps {
	readonly opened: boolean;
	readonly kind: TrainingArtifactKindValue;
	readonly quantType: string;
	readonly isPending: boolean;
	readonly onClose: () => void;
	readonly onKindChange: (value: TrainingArtifactKindValue) => void;
	readonly onQuantTypeChange: (value: string) => void;
	readonly onStart: () => void;
}

export function TrainingArtifactExportDialog(props: TrainingArtifactExportDialogProps) {
	const { t } = useTranslation();
	return (
		<DialogShell onClose={props.onClose} opened={props.opened} title={t("training.artifacts.exportTitle", "Export this run")}>
			<Stack gap="sm">
				<Select
					data={[
						{ value: "MergedGguf", label: t("training.artifacts.kind.MergedGguf", "Merged model") },
						{ value: "AdapterGguf", label: t("training.artifacts.kind.AdapterGguf", "Adapter only") },
					]}
					label={t("training.artifacts.kindLabel", "What to export")}
					onChange={(value) => props.onKindChange((value ?? "MergedGguf") as TrainingArtifactKindValue)}
					value={props.kind}
				/>
				{props.kind === "MergedGguf" ? (
					<Select
						data={[...trainingExportQuantizations]}
						label={t("training.artifacts.quantLabel", "Quantization")}
						onChange={(value) => props.onQuantTypeChange(value ?? defaultTrainingExportQuantization)}
						value={props.quantType}
					/>
				) : (
					<Text c="dimmed" size="sm">
						{t(
							"training.artifacts.adapterNote",
							"An adapter is always exported at F16 and is served on top of the base model it was trained against.",
						)}
					</Text>
				)}
				<Button loading={props.isPending} onClick={props.onStart}>
					{t("training.artifacts.startExport", "Start export")}
				</Button>
			</Stack>
		</DialogShell>
	);
}
