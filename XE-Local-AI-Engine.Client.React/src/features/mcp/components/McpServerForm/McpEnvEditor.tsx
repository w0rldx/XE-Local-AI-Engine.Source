import { ActionIcon, Button, Group, Stack, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import type { McpEnvRow } from "@/features/mcp/hooks/useMcpEnvRows";
import { maskedEnvValue } from "@/features/mcp/models/McpServerModels";

interface McpEnvEditorProps {
	rows: readonly McpEnvRow[];
	errors: Record<string, string>;
	onKeyChange: (id: string, key: string) => void;
	onValueChange: (id: string, value: string) => void;
	onAdd: () => void;
	onRemove: (id: string) => void;
}

// Key/value editor for stdio environment variables. env carries secrets, so the value inputs are rendered as
// plain text here (the user is the operator on their own node) but are encrypted at rest on save. Rows are
// keyed by their stable client id; the position index is used only to look up the validation error (whose Zod
// path is positional) and to build deterministic test ids. Each row wraps rather than forcing one line: the two
// inputs plus the remove button do not fit a phone-width dialog, so they carry flex bases and break onto a second
// line instead of overflowing.
export function McpEnvEditor({ rows, errors, onKeyChange, onValueChange, onAdd, onRemove }: McpEnvEditorProps) {
	const { t } = useTranslation();

	return (
		<Stack gap="xs" data-testid="mcp-form-env">
			<Group justify="space-between" align="center">
				<Text size="sm" fw={500}>
					{t("pages.mcp.form.env.label", "Environment variables")}
				</Text>
				<Button size="xs" variant="subtle" leftSection={<IconPlus size={14} />} onClick={onAdd} data-testid="mcp-form-env-add">
					{t("pages.mcp.form.env.add", "Add variable")}
				</Button>
			</Group>
			{rows.length === 0 ? <EmptyState size="xs" message={t("pages.mcp.form.env.empty", "No environment variables.")} /> : null}
			{rows.map((row, index) => (
				<Group key={row.id} gap="xs" align="flex-start" data-testid={`mcp-form-env-row-${index}`}>
					<TextInput
						placeholder={t("pages.mcp.form.env.keyPlaceholder", "KEY")}
						aria-label={t("pages.mcp.form.env.keyAria", "Variable {{index}} key", { index: index + 1 })}
						value={row.key}
						error={errors[`env.${index}.key`]}
						onChange={(event) => onKeyChange(row.id, event.currentTarget.value)}
						style={{ flex: "1 1 140px" }}
						data-testid={`mcp-form-env-key-${index}`}
					/>
					<TextInput
						placeholder={
							row.masked
								? t("pages.mcp.form.env.maskedPlaceholder", "unchanged — enter a new value to replace")
								: t("pages.mcp.form.env.valuePlaceholder", "value")
						}
						// Never the masked placeholder above: it is a sentence about this row's state, which would make a poor
						// accessible name. The row number is what tells one identically-shaped row from the next.
						aria-label={t("pages.mcp.form.env.valueAria", "Variable {{index}} value", { index: index + 1 })}
						value={row.value === maskedEnvValue ? "" : row.value}
						onChange={(event) => onValueChange(row.id, event.currentTarget.value)}
						style={{ flex: "2 1 200px" }}
						data-testid={`mcp-form-env-value-${index}`}
					/>
					<ActionIcon
						variant="subtle"
						color="red"
						aria-label={t("pages.mcp.form.env.remove", "Remove variable {{index}}", { index: index + 1 })}
						onClick={() => onRemove(row.id)}
						data-testid={`mcp-form-env-remove-${index}`}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</Group>
			))}
		</Stack>
	);
}
