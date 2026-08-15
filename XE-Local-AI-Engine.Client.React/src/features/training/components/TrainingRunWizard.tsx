import { Alert, Badge, Button, Checkbox, Group, NumberInput, Select, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import type { TrainingRunOptionsView } from "@/features/training/models/TrainingModels";
import { formatBytes } from "@/features/training/models/TrainingModels";
import { useTrainingDatasets } from "@/features/training/queries/useTrainingDatasets";
import { useBaseArtifacts } from "@/features/training/queries/useTrainingQueries";
import { useCreateTrainingRun, useTrainingRunDefaults } from "@/features/training/queries/useTrainingRuns";

/**
 * The run wizard: dataset, base checkpoint, an explicit licensing acknowledgement, and the hyper-parameters the
 * backend computed for this box.
 *
 * The options are pre-filled from the defaults route rather than from constants, because what fits depends on the
 * card in the machine. A configuration the backend refuses is surfaced as its refusal — never silently shrunk, which
 * would make the recorded hyper-parameters a lie.
 */
export function TrainingRunWizard() {
	const { t } = useTranslation();
	const datasetsQuery = useTrainingDatasets();
	const artifactsQuery = useBaseArtifacts();
	const [datasetId, setDatasetId] = useState<string | null>(null);
	const [baseArtifactId, setBaseArtifactId] = useState<string | null>(null);
	const [licenseConfirmed, setLicenseConfirmed] = useState(false);
	const [options, setOptions] = useState<TrainingRunOptionsView | null>(null);

	const defaultsQuery = useTrainingRunDefaults(baseArtifactId);
	const defaults = defaultsQuery.data;
	const createMutation = useCreateTrainingRun();

	const readyDatasets = useMemo(() => (datasetsQuery.data ?? []).filter((dataset) => dataset.status === "Ready"), [datasetsQuery.data]);
	const readyArtifacts = useMemo(() => (artifactsQuery.data ?? []).filter((artifact) => artifact.status === "Ready"), [artifactsQuery.data]);
	const dataset = readyDatasets.find((item) => item.id === datasetId) ?? null;

	// A new checkpoint means new computed options and, more importantly, different licensing — so the acknowledgement
	// is dropped rather than carried over onto terms the operator has not seen.
	useEffect(() => {
		setOptions(defaults?.options ?? null);
		setLicenseConfirmed(false);
	}, [defaults?.options]);

	const rejection = defaultsQuery.error != null ? t("training.runs.wizard.checkpointUnreadable", "This checkpoint cannot be sized for training.") : defaults?.rejectionReason ?? null;
	const canStart = dataset != null && baseArtifactId != null && licenseConfirmed && options != null && defaults?.fits === true && !createMutation.isPending;

	const update = (key: keyof TrainingRunOptionsView, value: number): void => {
		setOptions((current) => (current == null ? current : { ...current, [key]: value }));
	};

	const start = (): void => {
		if (dataset == null || baseArtifactId == null || options == null) {
			return;
		}
		createMutation.mutate({
			body: {
				datasetId: dataset.id,
				// Pins what the operator inspected: a sample edit since then refuses the run rather than training on
				// a dataset that moved under the dialog.
				expectedDatasetVersion: dataset.version,
				baseArtifactId,
				licenseConfirmed: true,
				options,
			},
		});
	};

	return (
		<SectionCard title={t("training.runs.wizard.title", "Start a training run")}>
			<Stack gap="md">
				<Text c="dimmed" size="sm">
					{t(
						"training.runs.wizard.description",
						"A run trains a LoRA adapter from one dataset and one base checkpoint. It holds the whole GPU while it runs.",
					)}
				</Text>

				<Select
					data={readyDatasets.map((item) => ({ value: item.id, label: `${item.name} (${item.totalSampleCount})` }))}
					label={t("training.runs.wizard.dataset", "Dataset")}
					onChange={setDatasetId}
					placeholder={t("training.runs.wizard.datasetPlaceholder", "Pick a ready dataset")}
					value={datasetId}
				/>

				<Select
					data={readyArtifacts.map((item) => ({ value: item.id, label: item.repoId }))}
					label={t("training.runs.wizard.baseCheckpoint", "Base checkpoint")}
					onChange={setBaseArtifactId}
					placeholder={t("training.runs.wizard.baseCheckpointPlaceholder", "Pick a downloaded checkpoint")}
					value={baseArtifactId}
				/>

				{rejection == null ? null : (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} title={t("training.runs.wizard.doesNotFit", "This run does not fit")}>
						{rejection}
					</Alert>
				)}

				{defaults?.estimate == null ? null : (
					<Group gap="sm">
						<Badge variant="light">
							{t("training.runs.wizard.estimate", "About {{estimate}} VRAM of {{available}} free", {
								estimate: formatBytes(defaults.estimate.gpuBytes),
								available: defaults.vramKnown ? formatBytes(defaults.availableVramBytes) : t("training.runs.wizard.vramUnknown", "unknown"),
							})}
						</Badge>
						{defaults.estimate.experimental ? (
							<Badge color="yellow" variant="light">
								{t("training.runs.wizard.experimental", "Experimental size")}
							</Badge>
						) : null}
					</Group>
				)}

				{defaults?.license == null ? null : (
					<Stack gap="xs">
						<Text size="sm">{defaults.license.confirmationText}</Text>
						{defaults.license.metadataPresent ? null : (
							<Text c="dimmed" size="xs">
								{t(
									"training.runs.wizard.noLicenseMetadata",
									"No license metadata was found for this repository. Confirming records that fact.",
								)}
							</Text>
						)}
						<Checkbox
							checked={licenseConfirmed}
							label={t("training.runs.wizard.confirmLicense", "I confirm I may fine-tune these weights")}
							onChange={(event) => setLicenseConfirmed(event.currentTarget.checked)}
						/>
					</Stack>
				)}

				{options == null ? null : (
					<Group align="end" gap="sm" wrap="wrap">
						<NumberInput
							label={t("training.runs.wizard.maxSeqLength", "Sequence length")}
							max={32768}
							min={128}
							onChange={(value) => update("maxSeqLength", Number(value))}
							value={options.maxSeqLength}
							w={140}
						/>
						<NumberInput
							label={t("training.runs.wizard.batchSize", "Batch size")}
							max={64}
							min={1}
							onChange={(value) => update("perDeviceTrainBatchSize", Number(value))}
							value={options.perDeviceTrainBatchSize}
							w={120}
						/>
						<NumberInput
							label={t("training.runs.wizard.loraR", "LoRA rank")}
							max={256}
							min={1}
							onChange={(value) => update("loraR", Number(value))}
							value={options.loraR}
							w={120}
						/>
						<NumberInput
							label={t("training.runs.wizard.epochs", "Epochs")}
							max={50}
							min={1}
							onChange={(value) => update("epochs", Number(value))}
							value={options.epochs}
							w={110}
						/>
					</Group>
				)}

				{createMutation.isError ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />}>
						{t("training.runs.wizard.startFailed", "The run could not be started. Check the options and try again.")}
					</Alert>
				) : null}

				<Group>
					<Button disabled={!canStart} loading={createMutation.isPending} onClick={start}>
						{t("training.runs.wizard.start", "Start run")}
					</Button>
				</Group>
			</Stack>
		</SectionCard>
	);
}
