import { Badge, Button, Group, Stack, Table, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { SlashCommand } from "@/features/commands/models/CommandModels";

interface CommandListProps {
	commands: readonly SlashCommand[];
	isMutating: boolean;
	onEdit: (id: string) => void;
	onDelete: (command: SlashCommand) => void;
}

export function CommandList({ commands, isMutating, onEdit, onDelete }: CommandListProps) {
	const { t } = useTranslation();
	if (commands.length === 0) {
		return <Text c="dimmed">{t("pages.commands.list.empty")}</Text>;
	}

	return (
		<Table.ScrollContainer minWidth={620}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.commands.list.command")}</Table.Th>
						<Table.Th>{t("pages.commands.list.action")}</Table.Th>
						<Table.Th ta="right">{t("common.actions", "Actions")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{commands.map((command) => (
						<Table.Tr key={command.id ?? `builtin-${command.name}`} data-testid={`command-row-${command.name}`}>
							<Table.Td>
								<Stack gap={2}>
									<Group gap="xs">
										<Text fw={600}>/{command.name}</Text>
										{command.source === "builtIn" ? <Badge size="sm">{t("pages.commands.list.builtIn")}</Badge> : null}
									</Group>
									{command.description ? <Text size="sm" c="dimmed">{command.description}</Text> : null}
								</Stack>
							</Table.Td>
							<Table.Td>{t("pages.commands.form.action.sendPrompt")}</Table.Td>
							<Table.Td>
								<Group justify="flex-end" gap="xs">
									{command.source === "custom" && command.id ? (
										<>
											<Button size="xs" variant="subtle" disabled={isMutating} onClick={() => onEdit(command.id ?? "")}>{t("common.edit")}</Button>
											<Button size="xs" color="red" variant="subtle" disabled={isMutating} onClick={() => onDelete(command)}>{t("common.delete")}</Button>
										</>
									) : null}
								</Group>
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}
