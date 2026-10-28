import type { ReactNode } from "react";
import type { useTranslation } from "react-i18next";

import { NodeSettingsFieldLabel } from "@/features/node-settings/components/NodeSettingsFieldLabel";
import type { NodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";

type Translate = ReturnType<typeof useTranslation>["t"];

// A field label with the "Needs restart" badge when the node reports the field as restart-gated.
export function nodeSettingsFieldLabel(t: Translate, field: keyof NodeSettingsFieldsForm, label: string): ReactNode {
	return <NodeSettingsFieldLabel t={t} field={field} label={label} />;
}

// The form's label for a wire field name, for surfaces outside the form (the pending-restart banner). Two fields are
// labeled under a different key; a field this client does not know shows its wire name.
const labelKeyAliases: Readonly<Record<string, string>> = {
	speculativeDraftModelName: "speculativeDraftModel",
	llamaChatCacheRamMiB: "llamaChatCacheRam",
};

export function nodeSettingsFieldLabelText(t: Translate, field: string): string {
	return t(`pages.nodeSettings.fields.${labelKeyAliases[field] ?? field}.label`, field);
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
