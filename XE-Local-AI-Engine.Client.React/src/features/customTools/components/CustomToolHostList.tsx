import { ActionIcon, Button, Group, Stack, Text, TextInput } from "@mantine/core";
import { IconPlus, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { useEditableRowKeys } from "@/features/customTools/components/CustomToolEditorShared";

interface CustomToolHostListProps {
	value: readonly string[];
	onChange: (next: string[]) => void;
	// "allowed" edits allowedHosts; "private" edits privateHosts, the per-tool LAN egress exception (ADR 0021).
	variant?: "allowed" | "private";
	rowError?: (index: number) => string | undefined;
}

// Host list editor: one host per row. allowedHosts is required when the URL host itself is templated; privateHosts
// lists the LAN hosts this tool may reach. Both guards run server-side too.
export function CustomToolHostList({ value, onChange, variant = "allowed", rowError }: CustomToolHostListProps) {
	const { t } = useTranslation();
	const { rowKeys, appendRowKey, removeRowKey } = useEditableRowKeys(value.length);
	const isPrivate = variant === "private";
	const testid = isPrivate ? "custom-tool-form-http-private-host" : "custom-tool-form-http-host";
	const labels = isPrivate
		? {
				title: t("pages.customTools.form.http.privateHosts", "Private hosts"),
				hint: t(
					"pages.customTools.form.http.privateHostsHint",
					"LAN hosts this tool may reach despite the private-address block: an exact host name or IP address, each also listed under Allowed hosts. Loopback, link-local, metadata and multicast addresses stay blocked.",
				),
				add: t("pages.customTools.form.http.addPrivateHost", "Add private host"),
				placeholder: "192.168.1.20",
				aria: (index: number) => t("pages.customTools.form.http.privateHostAria", "Private host {{index}}", { index }),
				remove: (index: number) => t("pages.customTools.form.http.removePrivateHost", "Remove private host {{index}}", { index }),
			}
		: {
				title: t("pages.customTools.form.http.allowedHosts", "Allowed hosts"),
				hint: t(
					"pages.customTools.form.http.allowedHostsHint",
					"Required when the URL host is itself templated: the request may only reach a host on this list.",
				),
				add: t("pages.customTools.form.http.addHost", "Add host"),
				placeholder: "api.example.com",
				// The placeholder is an example host, not a name, and the section heading is the same for every row —
				// the accessible name numbers the row through one interpolated string.
				aria: (index: number) => t("pages.customTools.form.http.hostAria", "Allowed host {{index}}", { index }),
				remove: (index: number) => t("pages.customTools.form.http.removeHost", "Remove allowed host {{index}}", { index }),
			};
	const addHost = (): void => {
		appendRowKey();
		onChange([...value, ""]);
	};
	const removeHost = (index: number): void => {
		removeRowKey(index);
		onChange(value.filter((_, position) => position !== index));
	};

	return (
		<Stack gap="xs" data-testid={`${testid}s`}>
			<Group justify="space-between" align="center">
				<Text size="sm" fw={500}>
					{labels.title}
				</Text>
				<Button size="xs" variant="subtle" leftSection={<IconPlus size={14} />} onClick={addHost} data-testid={`${testid}-add`}>
					{labels.add}
				</Button>
			</Group>
			<Text size="xs" c="dimmed">
				{labels.hint}
			</Text>
			{value.map((host, index) => (
				<Group key={rowKeys[index]} gap="xs" align="flex-start" wrap="nowrap">
					<TextInput
						placeholder={labels.placeholder}
						aria-label={labels.aria(index + 1)}
						value={host}
						error={rowError?.(index)}
						onChange={(event) => onChange(value.map((existing, i) => (i === index ? event.currentTarget.value : existing)))}
						style={{ flex: 1 }}
						data-testid={`${testid}-${index}`}
					/>
					<ActionIcon
						variant="subtle"
						color="red"
						aria-label={labels.remove(index + 1)}
						onClick={() => removeHost(index)}
						data-testid={`${testid}-remove-${index}`}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</Group>
			))}
		</Stack>
	);
}
