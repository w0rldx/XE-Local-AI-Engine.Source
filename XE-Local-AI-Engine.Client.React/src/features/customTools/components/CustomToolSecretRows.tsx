import { ActionIcon, Button, Checkbox, Group, Stack, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { StoredSecretInput } from "@/core/ui/components/StoredSecretInput/StoredSecretInput";
import { useEditableRowKeys } from "@/features/customTools/components/CustomToolEditorShared";
import { CUSTOM_TOOL_SECRET_SENTINEL, type SecretRow } from "@/features/customTools/models/CustomToolModels";

interface SecretRowsProps {
	title: string;
	addLabel: string;
	emptyLabel: string;
	testid: string;
	rows: readonly SecretRow[];
	/** Row names the node already stores a secret for; a row cleared in this session still counts, its value is "". */
	storedSecrets: readonly string[];
	onAdd: () => void;
	onRemove: (index: number) => void;
	onPatch: (index: number, patch: Partial<SecretRow>) => void;
}

// Shared name/value/isSecret row editor for HTTP headers and command env. A secret row's value box is the shared
// `StoredSecretInput`: a stored secret comes back as the sentinel, shows a "stored" hint, survives an unedited save, and
// is removed only through that control's explicit Clear. Marking a fresh row secret only affects how it is stored — the
// value input stays plain (operator on own node).
export function CustomToolSecretRows({
	title,
	addLabel,
	emptyLabel,
	testid,
	rows,
	storedSecrets,
	onAdd,
	onRemove,
	onPatch,
}: SecretRowsProps) {
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
				// Every row carries the same three boxes, so each accessible name numbers its row. The list itself is not
				// named: a tool is either HttpFetch or Command, so headers and env never render at once.
				const rowNumber = index + 1;
				return (
					<Group key={rowKeys[index]} gap="xs" align="flex-start" data-testid={`${testid}-row-${index}`}>
						<TextInput
							placeholder={t("pages.customTools.form.secretRows.namePlaceholder", "Name")}
							aria-label={t("pages.customTools.form.secretRows.nameAria", "Row {{index}} name", { index: rowNumber })}
							value={row.name}
							onChange={(event) => onPatch(index, { name: event.currentTarget.value })}
							style={{ flex: "2 1 140px" }}
							data-testid={`${testid}-name-${index}`}
						/>
						{row.isSecret ? (
							<StoredSecretInput
								stored={storedSecrets.includes(row.name)}
								sentinel={CUSTOM_TOOL_SECRET_SENTINEL}
								value={row.value}
								onChange={(value) => onPatch(index, { value })}
								storedPlaceholder={t("pages.customTools.form.secretRows.storedPlaceholder", "•••• stored — leave to keep")}
								placeholder={t("pages.customTools.form.secretRows.valuePlaceholder", "Value")}
								aria-label={t("pages.customTools.form.secretRows.valueAria", "Row {{index}} value", { index: rowNumber })}
								style={{ flex: "3 1 200px" }}
								data-testid={`${testid}-value-${index}`}
							/>
						) : (
							<TextInput
								placeholder={t("pages.customTools.form.secretRows.valuePlaceholder", "Value")}
								aria-label={t("pages.customTools.form.secretRows.valueAria", "Row {{index}} value", { index: rowNumber })}
								value={row.value}
								onChange={(event) => onPatch(index, { value: event.currentTarget.value })}
								style={{ flex: "3 1 200px" }}
								data-testid={`${testid}-value-${index}`}
							/>
						)}
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
							aria-label={t("pages.customTools.form.secretRows.remove", "Remove row {{index}}", { index: rowNumber })}
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
