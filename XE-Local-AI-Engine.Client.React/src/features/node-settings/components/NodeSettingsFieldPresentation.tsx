import { Badge, Group } from "@mantine/core";
import type { ReactNode } from "react";
import type { useTranslation } from "react-i18next";

import {
	type NodeSettingsFieldsForm,
	restartGatedNodeSettingsFields,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

type Translate = ReturnType<typeof useTranslation>["t"];

// A field label, with a "Needs restart" badge when the field is restart-gated. The badge sits in the label so it is
// read with the field's accessible name, and the save bar counts the same set (restartGatedNodeSettingsFields).
export function nodeSettingsFieldLabel(t: Translate, field: keyof NodeSettingsFieldsForm, label: string): ReactNode {
	if (!restartGatedNodeSettingsFields.has(field)) {
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

export function nodeSettingsFieldError(
	t: Translate,
	errors: Readonly<Record<string, string>>,
	field: string,
): string | undefined {
	const code = errors[field];
	return code === undefined ? undefined : t(`pages.nodeSettings.fields.errors.${code}`, "Invalid value.");
}

// "Allowed range: a–b unit." for a field shown in `unit` with the given display scale. A bound that is not a clean
// display value (1 s is 0.0166… minutes) is written in the wire unit instead, so the text never reads "0.02 minutes".
export function nodeSettingsAllowedRange(
	t: Translate,
	bounds: { readonly min: number; readonly max: number },
	unit: string,
	scale = 1,
	wireUnit = "",
): string {
	const unitSuffix = unit === "" ? "" : ` ${unit}`;
	const display = (value: number): number => value / scale;
	const isClean = (value: number): boolean => Math.round(display(value) * 100) / 100 === display(value);
	const max = `${Math.round(display(bounds.max) * 100) / 100}${unitSuffix}`;
	// "a–b unit", or "a s–b unit" when the lower bound has to be written in the wire unit.
	const min =
		isClean(bounds.min) || wireUnit === "" ? `${Math.round(display(bounds.min) * 100) / 100}` : `${bounds.min} ${wireUnit}`;
	return `${t("pages.nodeSettings.fields.allowedRange", "Allowed range")}: ${min}–${max}.`;
}
