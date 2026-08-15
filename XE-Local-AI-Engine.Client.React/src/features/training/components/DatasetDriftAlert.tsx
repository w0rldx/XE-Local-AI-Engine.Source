import { Alert } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { datasetDriftState } from "@/features/training/models/TrainingModels";
import { useTrainingDatasets } from "@/features/training/queries/useTrainingDatasets";

interface DatasetDriftAlertProps {
	datasetId: string;
	/** The fingerprint frozen when the run or evaluation took its hold-out set. */
	frozenFingerprint: string | null;
	context: "run" | "evaluation";
}

/**
 * Warns when the dataset has moved on since a run or an evaluation froze its hold-out set. Reviewing a single sample
 * bumps the dataset fingerprint, so this is a routine, silent way for two "hold-out accuracy" numbers to stop being
 * about the same thing — the scores stay on screen, they just stop claiming to be comparable.
 */
export function DatasetDriftAlert({ datasetId, frozenFingerprint, context }: DatasetDriftAlertProps) {
	const { t } = useTranslation();
	const datasetsQuery = useTrainingDatasets();
	const drift = datasetDriftState(frozenFingerprint, datasetId, datasetsQuery.data);

	if (drift === "current") {
		return null;
	}

	return (
		<Alert color="yellow" data-testid="training-dataset-drift" icon={<IconAlertTriangle size={16} />}>
			{drift === "deleted"
				? t("training.drift.deleted", "The dataset this used has been deleted; its frozen hold-out set can no longer be compared.")
				: context === "run"
					? t(
							"training.drift.run",
							"The dataset was edited after this run froze its hold-out set; scores may not be comparable.",
						)
					: t(
							"training.drift.evaluation",
							"The dataset was edited after this evaluation froze its hold-out set; scores may not be comparable.",
						)}
		</Alert>
	);
}
