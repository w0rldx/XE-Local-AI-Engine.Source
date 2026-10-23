import { Badge, Group } from "@mantine/core";
import { type ReactNode, useContext } from "react";
import type { useTranslation } from "react-i18next";

import { RestartRequiredFieldsContext } from "@/features/node-settings/models/RestartRequiredFieldsContext";

interface Props {
	readonly t: ReturnType<typeof useTranslation>["t"];
	readonly field: string;
	readonly label: string;
}

// A field label, with a "Needs restart" badge when the node reports the field as restart-gated. The badge sits in the
// label so it is read with the field's accessible name, and the save bar counts the same set.
export function NodeSettingsFieldLabel({ t, field, label }: Props): ReactNode {
	if (!useContext(RestartRequiredFieldsContext).has(field)) {
		return label;
	}

	return (
		<Group component="span" gap={6} wrap="nowrap" style={{ display: "inline-flex" }}>
			{label}
			<Badge
				component="span"
				size="xs"
				variant="light"
				color="orange"
				title={t("pages.nodeSettings.fields.restartRequired", "Takes effect after the node restarts.")}
				data-testid={`node-settings-restart-badge-${field}`}
			>
				{t("pages.nodeSettings.fields.restartBadge", "Needs restart")}
			</Badge>
		</Group>
	);
}
