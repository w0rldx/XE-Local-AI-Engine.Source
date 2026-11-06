import { Paper, SimpleGrid } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { tensorSizeLabel } from "@/core/models/inspect/GgufInspectInsights";
import { ModelInspectMetadataItem } from "@/core/models/inspect/ModelInspectMetadataItem";
import { type ArchitectureTensor, valueRelationLabel } from "@/features/modelLab/models/ModelLabArchitecture";

// One tensor as the file stores it, beside the Hugging Face tensor it came from.
export function ArchitectureTensorDetails({ tensor }: { tensor: ArchitectureTensor }) {
	const { t } = useTranslation();
	return (
		<Paper withBorder={true} p="sm" data-testid="model-lab-architecture-tensor">
			<SimpleGrid cols={{ base: 1, sm: 2 }} spacing="sm">
				<ModelInspectMetadataItem label={t("pages.modelLab.architecture.ggufName", "GGUF name")} value={tensor.ggufName} />
				<ModelInspectMetadataItem
					label={t("pages.modelLab.architecture.hfName", "Hugging Face name")}
					value={tensor.hfName ?? t("pages.modelLab.architecture.noHfName", "None")}
				/>
				<ModelInspectMetadataItem
					label={t("pages.modelLab.architecture.shape", "Shape (GGUF order)")}
					value={tensor.shape.join(" × ")}
				/>
				<ModelInspectMetadataItem
					label={t("pages.models.inspect.columns.type", "Type")}
					value={tensor.ggmlType ?? t("pages.models.details.unknown", "Unknown")}
				/>
				<ModelInspectMetadataItem
					label={t("pages.models.inspect.columns.size", "Size")}
					value={tensorSizeLabel(t, tensor.byteSize)}
				/>
				<ModelInspectMetadataItem
					label={t("pages.modelLab.architecture.relation.label", "Value relation")}
					value={valueRelationLabel(t, tensor.valueRelation)}
				/>
			</SimpleGrid>
		</Paper>
	);
}
