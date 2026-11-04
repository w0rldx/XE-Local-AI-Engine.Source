import { Select, Stack } from "@mantine/core";
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { ModelInspectPanel } from "@/core/models/inspect/ModelInspectPanel";
import { LLAMACPP_PROVIDER } from "@/core/models/LocalModelProviders";
import { sameRepoVariants } from "@/core/models/ModelVariants";
import { ModelPickerNotice } from "@/features/modelLab/components/ModelPickerNotice";

// The Models dialog's Inspect panel for a model chosen here; only a llama.cpp model has a file to read.
export function InstalledModelInspector() {
	const { t } = useTranslation();
	const { data, status } = useQuery({ ...listLocalModelsOptions(), staleTime: 30_000 });
	const models = (data?.items ?? []).flatMap((item) =>
		item.provider === LLAMACPP_PROVIDER && item.modelName ? [{ modelName: item.modelName, provider: item.provider }] : [],
	);
	const [modelName, setModelName] = useState<string | null>(null);

	return (
		<Stack gap="md">
			<ModelPickerNotice status={status} modelCount={models.length} />
			<Select
				label={t("pages.modelLab.inspectModel", "Installed model")}
				placeholder={t("pages.directInference.modelPlaceholder", "Choose a model")}
				data={models.map((model) => model.modelName)}
				value={modelName}
				onChange={setModelName}
				searchable={true}
			/>
			{modelName !== null && (
				<ModelInspectPanel key={modelName} modelName={modelName} compareCandidates={sameRepoVariants(modelName, models)} />
			)}
		</Stack>
	);
}
