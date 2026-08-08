import { Combobox, Group, Text } from "@mantine/core";
import type { ComboboxStore } from "@mantine/core";
import type { ReactElement } from "react";
import { useTranslation } from "react-i18next";

import type { ChatCommandOption } from "@/features/chat/models/SlashCommandModels";
import { slashCommandOptionId } from "@/features/chat/models/SlashCommandResolver";

interface SlashCommandAutocompleteProps {
	store: ComboboxStore;
	options: readonly ChatCommandOption[];
	activeDescendantId?: string;
	target: ReactElement;
	onSelect: (option: ChatCommandOption) => void;
}

export function SlashCommandAutocomplete({ store, options, activeDescendantId, target, onSelect }: SlashCommandAutocompleteProps) {
	const { t } = useTranslation();
	return (
		<Combobox store={store} onOptionSubmit={(value) => {
			const option = options.find((candidate) => candidate.name === value);
			if (option) {
				onSelect(option);
			}
		}} withinPortal={true} position="top-start" width="target" resetSelectionOnOptionHover={false}>
			<Combobox.Target aria-activedescendant={activeDescendantId}>{target}</Combobox.Target>
			<Combobox.Dropdown data-testid="slash-command-menu">
				<Combobox.Options aria-label={t("pages.chat.commands.menuLabel")}>
					{options.map((option) => (
						<Combobox.Option id={slashCommandOptionId(option.name)} key={option.id ?? `builtin-${option.name}`} value={option.name} data-testid={`slash-command-option-${option.name}`}>
							<Group justify="space-between" wrap="nowrap">
								<Text fw={600}>/{option.name}</Text>
								{option.description ? <Text size="sm" c="dimmed" lineClamp={1}>{option.description}</Text> : null}
							</Group>
						</Combobox.Option>
					))}
				</Combobox.Options>
			</Combobox.Dropdown>
		</Combobox>
	);
}
