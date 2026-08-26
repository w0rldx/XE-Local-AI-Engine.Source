import { Text } from "@mantine/core";
import type { ReactNode } from "react";
import type { useTranslation } from "react-i18next";

import {
	type NodeSettingsFieldsForm,
	restartGatedNodeSettingsFields,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

type Translate = ReturnType<typeof useTranslation>["t"];
export function nodeSettingsRestartHint(t: Translate, field: keyof NodeSettingsFieldsForm): ReactNode {
	if (!restartGatedNodeSettingsFields.has(field)) {
		return null;
	}

	return (
		<Text component="span" size="xs" fw={600} data-testid={`node-settings-restart-hint-${field}`}>
			{" "}
			{t("pages.nodeSettings.fields.restartRequired", "Takes effect after the node restarts.")}
		</Text>
	);
}

export function nodeSettingsFieldError(
	t: Translate,
	errors: Readonly<Record<string, string>>,
	field: string,
): string | undefined {
	const code = errors[field];
	return code === undefined ? undefined : t(`pages.nodeSettings.fields.errors.${code}`, "Invalid value.");
}
