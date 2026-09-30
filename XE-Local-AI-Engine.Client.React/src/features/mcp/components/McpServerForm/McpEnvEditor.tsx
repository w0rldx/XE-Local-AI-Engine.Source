import { ActionIcon, Button, Group, Stack, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import type { McpEnvRow } from "@/features/mcp/hooks/useMcpEnvRows";
import { maskedEnvValue } from "@/features/mcp/models/McpServerModels";

interface McpEnvEditorProps {
	/** Which map this edits: stdio environment variables or HTTP request headers. Picks the labels and test ids. */
	kind: "env" | "headers";
	rows: readonly McpEnvRow[];
	errors: Record<string, string>;
	onKeyChange: (id: string, key: string) => void;
	onValueChange: (id: string, value: string) => void;
	onAdd: () => void;
	onRemove: (id: string) => void;
}

// Key/value editor for a secret-bearing map: stdio environment variables or HTTP request headers. Both are encrypted at
// rest and never returned, so values are password inputs and a stored value shows as an empty box with an "unchanged"
// placeholder. Rows are keyed by their stable client id; the position index is used only to look up the validation
// error (whose Zod path is positional) and to build deterministic test ids. Each row wraps rather than forcing one
// line: the two inputs plus the remove button do not fit a phone-width dialog, so they carry flex bases and break onto
// a second line instead of overflowing.
export function McpEnvEditor({ kind, rows, errors, onKeyChange, onValueChange, onAdd, onRemove }: McpEnvEditorProps) {
	const { t } = useTranslation();

	return (
		<Stack gap="xs" data-testid={`mcp-form-${kind}`}>
			<Group justify="space-between" align="center">
				<Text size="sm" fw={500}>
					{t(`pages.mcp.form.${kind}.label`)}
				</Text>
				<Button
					size="xs"
					variant="subtle"
					leftSection={<IconPlus size={14} />}
					onClick={onAdd}
					data-testid={`mcp-form-${kind}-add`}
				>
					{t(`pages.mcp.form.${kind}.add`)}
				</Button>
			</Group>
			{rows.length === 0 ? <EmptyState size="xs" message={t(`pages.mcp.form.${kind}.empty`)} /> : null}
			{rows.map((row, index) => (
				<Group key={row.id} gap="xs" align="flex-start" data-testid={`mcp-form-${kind}-row-${index}`}>
					<TextInput
						placeholder={t(`pages.mcp.form.${kind}.keyPlaceholder`)}
						aria-label={t(`pages.mcp.form.${kind}.keyAria`, { index: index + 1 })}
						value={row.key}
						error={errors[`${kind}.${index}.key`]}
						onChange={(event) => onKeyChange(row.id, event.currentTarget.value)}
						style={{ flex: "1 1 140px" }}
						data-testid={`mcp-form-${kind}-key-${index}`}
					/>
					<TextInput
						placeholder={
							row.masked
								? t("pages.mcp.form.env.maskedPlaceholder", "unchanged — enter a new value to replace")
								: t(`pages.mcp.form.${kind}.valuePlaceholder`)
						}
						// Never the masked placeholder above: it is a sentence about this row's state, which would make a poor
						// accessible name. The row number is what tells one identically-shaped row from the next.
						aria-label={t(`pages.mcp.form.${kind}.valueAria`, { index: index + 1 })}
						type="password"
						autoComplete="new-password"
						value={row.value === maskedEnvValue ? "" : row.value}
						onChange={(event) => onValueChange(row.id, event.currentTarget.value)}
						style={{ flex: "2 1 200px" }}
						data-testid={`mcp-form-${kind}-value-${index}`}
					/>
					<ActionIcon
						variant="subtle"
						color="red"
						aria-label={t(`pages.mcp.form.${kind}.remove`, { index: index + 1 })}
						onClick={() => onRemove(row.id)}
						data-testid={`mcp-form-${kind}-remove-${index}`}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</Group>
			))}
		</Stack>
	);
}
