import { Stack } from "@mantine/core";
import { IconSchool } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { BaseArtifactManager } from "@/features/training/components/BaseArtifactManager";
import { TrainingRunList } from "@/features/training/components/TrainingRunList";
import { TrainingRunWizard } from "@/features/training/components/TrainingRunWizard";
import { TrainingRuntimeCard } from "@/features/training/components/TrainingRuntimeCard";

/**
 * Fine-tuning page: the two prerequisites a run needs (the pinned Python runtime and a downloaded base checkpoint),
 * then the run wizard and the run list.
 */
export function TrainingPage() {
	const { t } = useTranslation();

	return (
		<PageShell>
			<PageHeader
				icon={<IconSchool size={24} />}
				subtitle={t(
					"pages.training.subtitle",
					"Fine-tune a local model on your own data. Training holds the whole GPU while it runs, so no chat, image or benchmark work happens at the same time.",
				)}
				title={t("pages.training.title", "Training")}
			/>

			<Stack gap="lg">
				<TrainingRuntimeCard />
				<BaseArtifactManager />
				<TrainingRunWizard />
				<TrainingRunList />
			</Stack>
		</PageShell>
	);
}
