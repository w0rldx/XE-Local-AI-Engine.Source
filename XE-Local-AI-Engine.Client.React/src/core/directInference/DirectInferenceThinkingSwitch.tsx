import { Switch } from "@mantine/core";
import { useTranslation } from "react-i18next";

interface DirectInferenceThinkingSwitchProps {
	checked: boolean;
	onChange: (checked: boolean) => void;
	disabled?: boolean;
}

/** Turns a reasoning llama.cpp model's thinking off for one request; unchecked keeps the model's default. */
export function DirectInferenceThinkingSwitch({ checked, onChange, disabled }: DirectInferenceThinkingSwitchProps) {
	const { t } = useTranslation();
	return (
		<Switch
			label={t("pages.directInference.thinkingOff", "Thinking off")}
			description={t(
				"pages.directInference.thinkingOffDescription",
				"The model answers without thinking first, so the output limit goes to the answer. Unchecked, the model's default applies.",
			)}
			checked={checked}
			onChange={(event) => onChange(event.currentTarget.checked)}
			disabled={disabled}
			data-testid="direct-inference-thinking-off"
		/>
	);
}
