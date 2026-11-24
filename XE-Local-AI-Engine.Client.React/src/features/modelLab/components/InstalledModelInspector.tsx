import { useQuery } from "@tanstack/react-query";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { ModelInspectPanel } from "@/core/models/inspect/ModelInspectPanel";
import { sameRepoVariants } from "@/core/models/ModelVariants";
import { installedGgufModels } from "@/features/modelLab/models/ModelLabNavigation";

// The Models dialog's Inspect panel for the page's model, once the Inspect tab has shown it.
export function InstalledModelInspector({ modelName }: { modelName: string | null }) {
	const { data } = useQuery({ ...listLocalModelsOptions(), staleTime: 30_000 });
	if (modelName === null) {
		return null;
	}
	return (
		<ModelInspectPanel
			key={modelName}
			modelName={modelName}
			compareCandidates={sameRepoVariants(modelName, installedGgufModels(data?.items))}
		/>
	);
}
