import { NumberInput } from "@mantine/core";
import { useTranslation } from "react-i18next";

import {
	nodeSettingsAllowedRange,
	nodeSettingsFieldError,
	nodeSettingsFieldLabel,
} from "@/features/node-settings/components/NodeSettingsFieldPresentation";
import {
	type NodeSettingsFieldsForm,
	type NumericBounds,
	nodeSettingsScaleOf,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

type NumericFormField = {
	[K in keyof NodeSettingsFieldsForm]: NodeSettingsFieldsForm[K] extends number | string ? K : never;
}[keyof NodeSettingsFieldsForm];

interface Props {
	readonly field: NumericFormField;
	readonly label: string;
	// Extra guidance after the allowed-range sentence.
	readonly description?: string;
	// Wire-unit bounds; the display scale of the field converts them.
	readonly bounds: NumericBounds;
	// The display unit ("minutes", "%", "GB"...) and, for a scaled field, the short wire unit used when a bound is not a
	// clean display value.
	readonly unit?: string;
	readonly wireUnit?: string;
	readonly form: NodeSettingsFieldsForm;
	readonly errors: Readonly<Record<string, string>>;
	readonly onChange: <K extends keyof NodeSettingsFieldsForm>(field: K, value: NodeSettingsFieldsForm[K]) => void;
	readonly disabled?: boolean;
	readonly testId: string;
}

// One draft-bound number, with its restart badge, range sentence, unit suffix and validation error.
export function NodeSettingsNumberField({
	field,
	label,
	description,
	bounds,
	unit = "",
	wireUnit = "",
	form,
	errors,
	onChange,
	disabled,
	testId,
}: Props) {
	const { t } = useTranslation();
	const scale = nodeSettingsScaleOf(field);
	const range = nodeSettingsAllowedRange(t, bounds, unit, scale, wireUnit);

	return (
		<NumberInput
			label={nodeSettingsFieldLabel(t, field, label)}
			description={description === undefined ? range : [range, description].filter((part) => part !== "").join(" ")}
			suffix={unit === "" ? undefined : unit === "%" ? " %" : ` ${unit}`}
			min={bounds.min / scale}
			max={bounds.max / scale}
			allowDecimal={scale !== 1}
			decimalScale={scale === 1 ? undefined : 2}
			disabled={disabled}
			value={form[field]}
			onChange={(value) => onChange(field, value)}
			error={nodeSettingsFieldError(t, errors, field)}
			data-testid={testId}
		/>
	);
}
