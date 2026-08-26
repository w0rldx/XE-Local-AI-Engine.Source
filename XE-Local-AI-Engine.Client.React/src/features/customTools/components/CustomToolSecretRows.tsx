import { ActionIcon, Button, Checkbox, Group, Stack, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { useEditableRowKeys } from "@/features/customTools/components/CustomToolEditorShared";
import { CUSTOM_TOOL_SECRET_SENTINEL } from "@/features/customTools/models/CustomToolModels";

interface SecretRow {
	readonly name: string;
	readonly value: string;
	readonly isSecret: boolean;
}

interface SecretRowsProps {
	title: string;
	addLabel: string;
	emptyLabel: string;
	testid: string;
	rows: readonly SecretRow[];
	onAdd: () => void;
	onRemove: (index: number) => void;
	onPatch: (index: number, patch: Partial<SecretRow>) => void;
}

// Shared name/value/isSecret row editor for HTTP headers and command env. A stored secret comes back as the sentinel;
// the row shows a "stored" hint and leaves it in place so an unedited save keeps the secret. Editing the value replaces
// it. Marking a fresh row secret only affects how it is stored — the value input stays plain (operator on own node).
export function CustomToolSecretRows({ title, addLabel, emptyLabel, testid, rows, onAdd, onRemove, onPatch }: SecretRowsProps) {
	const { t } = useTranslation();
	const { rowKeys, appendRowKey, removeRowKey } = useEditableRowKeys(rows.length);
	const addRow = (): void => {
		appendRowKey();
		onAdd();
	};
	const removeRow = (index: number): void => {
		removeRowKey(index);
		onRemove(index);
	};

	return (
		<Stack gap="xs" data-testid={testid}>
			<Group justify="space-between" align="center">
				<Text size="sm" fw={500}>
					{title}
				</Text>
				<Button size="xs" variant="subtle" leftSection={<IconPlus size={14} />} onClick={addRow} data-testid={`${testid}-add`}>
					{addLabel}
				</Button>
			</Group>
			{rows.length === 0 ? (
				<Text size="xs" c="dimmed">
					{emptyLabel}
				</Text>
			) : null}
			{rows.map((row, index) => {
				const isStoredSecret = row.isSecret && row.value === CUSTOM_TOOL_SECRET_SENTINEL;
				return (
					<Group key={rowKeys[index]} gap="xs" align="flex-start" data-testid={`${testid}-row-${index}`}>
						<TextInput
							placeholder={t("pages.customTools.form.secretRows.namePlaceholder", "Name")}
							value={row.name}
							onChange={(event) => onPatch(index, { name: event.currentTarget.value })}
							style={{ flex: "2 1 140px" }}
							data-testid={`${testid}-name-${index}`}
						/>
						<TextInput
							placeholder={
								isStoredSecret
									? t("pages.customTools.form.secretRows.storedPlaceholder", "•••• stored — leave to keep")
									: t("pages.customTools.form.secretRows.valuePlaceholder", "Value")
							}
							value={isStoredSecret ? "" : row.value}
							onChange={(event) => onPatch(index, { value: event.currentTarget.value })}
							style={{ flex: "3 1 200px" }}
							data-testid={`${testid}-value-${index}`}
						/>
						<Checkbox
							label={t("pages.customTools.form.secretRows.secret", "Secret")}
							checked={row.isSecret}
							onChange={(event) => {
								const checked = event.currentTarget.checked;
								// Clearing the sentinel when un-marking a stored secret avoids persisting the literal sentinel as a value.
								const nextValue = !checked && row.value === CUSTOM_TOOL_SECRET_SENTINEL ? "" : row.value;
								onPatch(index, { isSecret: checked, value: nextValue });
							}}
							mt={8}
							style={{ flexShrink: 0 }}
							data-testid={`${testid}-secret-${index}`}
						/>
						<ActionIcon
							variant="subtle"
							color="red"
							aria-label={t("pages.customTools.form.secretRows.remove", "Remove")}
							onClick={() => removeRow(index)}
							mt={4}
							style={{ flexShrink: 0 }}
							data-testid={`${testid}-remove-${index}`}
						>
							<IconTrash size={16} />
						</ActionIcon>
					</Group>
				);
			})}
		</Stack>
	);
}
