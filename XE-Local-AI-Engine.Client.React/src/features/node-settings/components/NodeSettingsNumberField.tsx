import { Box, Button, Group, NumberInput } from "@mantine/core";
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
	// For a field the node may leave unset: what it uses then. Blank shows it as the placeholder, and a set value gets a
	// "Use default" action that blanks the field (the save sends the reset sentinel).
	readonly shippedDefault?: number;
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
	shippedDefault,
}: Props) {
	const { t } = useTranslation();
	const scale = nodeSettingsScaleOf(field);
	const range = nodeSettingsAllowedRange(t, bounds, unit, scale, wireUnit);
	const isSet = String(form[field]).trim() !== "";

	return (
		<NumberInput
			placeholder={
				shippedDefault === undefined
					? undefined
					: t("pages.nodeSettings.fields.defaultValue", "Default: {{value}}", {
							value: unit === "" ? shippedDefault : `${shippedDefault} ${unit}`,
						})
			}
			// The wrapper stays mounted whether or not the field is set: swapping it in on the first keystroke would
			// remount the input and drop focus mid-typing.
			inputContainer={
				shippedDefault === undefined
					? undefined
					: (children) => (
							<Group gap="xs" wrap="nowrap">
								<Box flex={1}>{children}</Box>
								{isSet && (
									<Button
										variant="subtle"
										size="compact-sm"
										disabled={disabled}
										onClick={() => onChange(field, "")}
										data-testid={`${testId}-use-default`}
									>
										{t("pages.nodeSettings.fields.useDefault", "Use default")}
									</Button>
								)}
							</Group>
						)
			}
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
