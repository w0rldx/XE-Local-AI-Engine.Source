import { Badge, Button, Group, Stack, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { SlashCommand } from "@/core/commands/models/CommandModels";
import { ResponsiveTable } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";

interface CommandListProps {
	commands: readonly SlashCommand[];
	isMutating: boolean;
	onEdit: (id: string) => void;
	onDelete: (command: SlashCommand) => void;
}

export function CommandList({ commands, isMutating, onEdit, onDelete }: CommandListProps) {
	const { t } = useTranslation();

	return (
		<ResponsiveTable
			rows={commands}
			rowKey={(command) => command.id ?? `builtin-${command.name}`}
			rowTestId={(command) => `command-row-${command.name}`}
			minWidth={620}
			empty={<Text c="dimmed">{t("pages.commands.list.empty")}</Text>}
			columns={[
				{
					key: "command",
					header: t("pages.commands.list.command"),
					primary: true,
					cell: (command) => (
						<Stack gap={2}>
							<Group gap="xs">
								<Text fw={600}>/{command.name}</Text>
								{command.source === "builtIn" ? <Badge size="sm">{t("pages.commands.list.builtIn")}</Badge> : null}
							</Group>
							{command.description ? (
								<Text size="sm" c="dimmed">
									{/* A built-in's description is the node's English text, so the client labels it by name. */}
									{command.source === "builtIn"
										? t(`pages.commands.builtInDescriptions.${command.name}`, command.description)
										: command.description}
								</Text>
							) : null}
						</Stack>
					),
				},
				{
					key: "action",
					header: t("pages.commands.list.action"),
					style: { whiteSpace: "nowrap" },
					cell: () => t("pages.commands.form.action.sendPrompt"),
				},
			]}
			actionsHeader={t("common.actions", "Actions")}
			// Built-ins cannot be edited: with no custom command there is no actions column at all, and a built-in row
			// beside custom ones shows a dash rather than an empty cell.
			actions={
				commands.some(isEditable)
					? (command) =>
							isEditable(command) ? (
								<>
									<Button size="xs" variant="subtle" disabled={isMutating} onClick={() => onEdit(command.id ?? "")}>
										{t("common.edit")}
									</Button>
									<Button size="xs" color="red" variant="subtle" disabled={isMutating} onClick={() => onDelete(command)}>
										{t("common.delete")}
									</Button>
								</>
							) : (
								<Text c="dimmed">—</Text>
							)
					: undefined
			}
		/>
	);
}

function isEditable(command: SlashCommand): boolean {
	return command.source === "custom" && Boolean(command.id);
}
