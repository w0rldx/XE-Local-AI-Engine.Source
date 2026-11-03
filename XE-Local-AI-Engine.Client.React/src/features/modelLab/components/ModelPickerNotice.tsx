import { Alert } from "@mantine/core";
import { useTranslation } from "react-i18next";

interface ModelPickerNoticeProps {
	status: "pending" | "error" | "success";
	modelCount: number;
}

// Says why a Model Lab picker is empty, instead of leaving an empty dropdown.
export function ModelPickerNotice({ status, modelCount }: ModelPickerNoticeProps) {
	const { t } = useTranslation();
	if (status === "error") {
		return (
			<Alert color="red" data-testid="model-lab-models-failed">
				{t("pages.modelLab.modelsFailed", "The installed models could not be loaded.")}
			</Alert>
		);
	}
	return status === "success" && modelCount === 0 ? (
		<Alert color="yellow" data-testid="model-lab-no-models">
			{t("pages.modelLab.noModels", "No installed llama.cpp model is available. Install one under Models first.")}
		</Alert>
	) : null;
}
