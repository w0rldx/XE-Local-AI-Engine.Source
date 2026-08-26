import { ActionIcon, Button, Checkbox, Group, Select, Stack, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { type CustomToolEditorSectionProps, useEditableRowKeys } from "@/features/customTools/components/CustomToolEditorShared";
import { errorAt } from "@/features/customTools/models/CustomToolFormErrors";
import {
	CUSTOM_TOOL_PARAMETER_TYPES,
	type CustomToolParameter,
	type CustomToolParameterType,
} from "@/features/customTools/models/CustomToolModels";

// Parameter builder: rows of name / type / description / required, editing the declared inputs a Parameterized tool
// exposes to the model. Stable local keys preserve each controlled row when a sibling is removed.
export function ParameterBuilder({ values, errors, update }: CustomToolEditorSectionProps) {
	const { t } = useTranslation();
	const { rowKeys, appendRowKey, removeRowKey } = useEditableRowKeys(values.parameters.length);

	const addRow = () => {
		appendRowKey();
		update((current) => ({
			...current,
			parameters: [...current.parameters, { name: "", type: "string", description: "", required: true }],
		}));
	};

	const removeRow = (index: number) => {
		removeRowKey(index);
		update((current) => ({ ...current, parameters: current.parameters.filter((_, i) => i !== index) }));
	};

	const patchRow = (index: number, patch: Partial<CustomToolParameter>) =>
		update((current) => ({
			...current,
			parameters: current.parameters.map((parameter, i) => (i === index ? { ...parameter, ...patch } : parameter)),
		}));

	return (
		<Stack gap="xs" data-testid="custom-tool-form-parameters">
			<Group justify="space-between" align="center">
				<Text size="sm" fw={500}>
					{t("pages.customTools.form.parameters.label", "Parameters")}
				</Text>
				<Button
					size="xs"
					variant="subtle"
					leftSection={<IconPlus size={14} />}
					onClick={addRow}
					data-testid="custom-tool-form-parameter-add"
				>
					{t("pages.customTools.form.parameters.add", "Add parameter")}
				</Button>
			</Group>
			{values.parameters.length === 0 ? (
				<Text size="xs" c="dimmed">
					{t("pages.customTools.form.parameters.empty", "No parameters declared yet.")}
				</Text>
			) : null}
			{values.parameters.map((parameter, index) => (
				<Group key={rowKeys[index]} gap="xs" align="flex-start" data-testid={`custom-tool-form-parameter-row-${index}`}>
					<TextInput
						placeholder={t("pages.customTools.form.parameters.namePlaceholder", "city")}
						value={parameter.name}
						error={
							errorAt(errors, `parameters.${index}.name`)
								? t("pages.customTools.form.parameters.nameInvalid", "Identifier only")
								: undefined
						}
						onChange={(event) => patchRow(index, { name: event.currentTarget.value })}
						style={{ flex: "2 1 140px" }}
						data-testid={`custom-tool-form-parameter-name-${index}`}
					/>
					<Select
						value={parameter.type}
						data={CUSTOM_TOOL_PARAMETER_TYPES.map((type) => ({ label: type, value: type }))}
						onChange={(value) => patchRow(index, { type: (value ?? "string") as CustomToolParameterType })}
						style={{ flex: "1 1 110px" }}
						allowDeselect={false}
						data-testid={`custom-tool-form-parameter-type-${index}`}
					/>
					<TextInput
						placeholder={t("pages.customTools.form.parameters.descriptionPlaceholder", "description")}
						value={parameter.description}
						onChange={(event) => patchRow(index, { description: event.currentTarget.value })}
						style={{ flex: "3 1 200px" }}
						data-testid={`custom-tool-form-parameter-description-${index}`}
					/>
					<Checkbox
						label={t("pages.customTools.form.parameters.required", "Required")}
						checked={parameter.required}
						onChange={(event) => patchRow(index, { required: event.currentTarget.checked })}
						mt={8}
						style={{ flexShrink: 0 }}
						data-testid={`custom-tool-form-parameter-required-${index}`}
					/>
					<ActionIcon
						variant="subtle"
						color="red"
						aria-label={t("pages.customTools.form.parameters.remove", "Remove parameter")}
						onClick={() => removeRow(index)}
						mt={4}
						style={{ flexShrink: 0 }}
						data-testid={`custom-tool-form-parameter-remove-${index}`}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</Group>
			))}
		</Stack>
	);
}
