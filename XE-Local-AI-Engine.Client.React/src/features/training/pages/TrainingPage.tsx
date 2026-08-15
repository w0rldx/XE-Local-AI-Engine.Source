import { Stack } from "@mantine/core";
import { IconSchool } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { BaseArtifactManager } from "@/features/training/components/BaseArtifactManager";
import { TrainingRuntimeCard } from "@/features/training/components/TrainingRuntimeCard";

/**
 * Fine-tuning page. Slice 2 ships the two prerequisites a run needs before it can exist: the pinned Python runtime,
 * and the base checkpoints a run trains from. The run wizard and run list arrive with the runs module.
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
			</Stack>
		</PageShell>
	);
}
