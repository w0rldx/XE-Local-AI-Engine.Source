import { ActionIcon, Button, Group, Stack, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { useEditableRowKeys } from "@/features/customTools/components/CustomToolEditorShared";

// allowedHosts editor: one host per row. Required when the URL host itself is templated; the guard runs server-side.
export function CustomToolHostList({ value, onChange }: { value: readonly string[]; onChange: (next: string[]) => void }) {
	const { t } = useTranslation();
	const { rowKeys, appendRowKey, removeRowKey } = useEditableRowKeys(value.length);
	const addHost = (): void => {
		appendRowKey();
		onChange([...value, ""]);
	};
	const removeHost = (index: number): void => {
		removeRowKey(index);
		onChange(value.filter((_, position) => position !== index));
	};

	return (
		<Stack gap="xs" data-testid="custom-tool-form-http-hosts">
			<Group justify="space-between" align="center">
				<Text size="sm" fw={500}>
					{t("pages.customTools.form.http.allowedHosts", "Allowed hosts")}
				</Text>
				<Button
					size="xs"
					variant="subtle"
					leftSection={<IconPlus size={14} />}
					onClick={addHost}
					data-testid="custom-tool-form-http-host-add"
				>
					{t("pages.customTools.form.http.addHost", "Add host")}
				</Button>
			</Group>
			<Text size="xs" c="dimmed">
				{t(
					"pages.customTools.form.http.allowedHostsHint",
					"Required when the URL host is itself templated: the request may only reach a host on this list.",
				)}
			</Text>
			{value.map((host, index) => (
				<Group key={rowKeys[index]} gap="xs" align="center" wrap="nowrap">
					<TextInput
						placeholder="api.example.com"
						value={host}
						onChange={(event) => onChange(value.map((existing, i) => (i === index ? event.currentTarget.value : existing)))}
						style={{ flex: 1 }}
						data-testid={`custom-tool-form-http-host-${index}`}
					/>
					<ActionIcon
						variant="subtle"
						color="red"
						aria-label={t("pages.customTools.form.http.removeHost", "Remove host")}
						onClick={() => removeHost(index)}
						data-testid={`custom-tool-form-http-host-remove-${index}`}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</Group>
			))}
		</Stack>
	);
}
