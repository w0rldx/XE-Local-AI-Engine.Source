import { Alert, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { ButtonLink } from "@/core/ui/components/ButtonLink/ButtonLink";

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
			<Stack gap="xs" align="flex-start">
				<Text size="sm">
					{t("pages.modelLab.noModels", "No installed llama.cpp model is available. Install one under Models first.")}
				</Text>
				<ButtonLink to="/models" size="compact-sm" variant="light" color="yellow" data-testid="model-lab-no-models-link">
					{t("pages.modelLab.openModels", "Open Models")}
				</ButtonLink>
			</Stack>
		</Alert>
	) : null;
}
