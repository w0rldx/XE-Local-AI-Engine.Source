import { useCombobox } from "@mantine/core";
import { useCallback, useEffect, useMemo, useState } from "react";

import type { ChatCommandOption } from "@/features/chat/models/SlashCommandModels";
import {
	getSlashCommandQuery,
	matchSlashCommands,
	slashCommandOptionId,
	slashInputSignature,
} from "@/features/chat/models/SlashCommandResolver";

interface UseSlashCommandAutocompleteInput {
	content: string;
	selectionStart: number;
	selectionEnd: number;
	interactive: boolean;
	isComposing: boolean;
	options: readonly ChatCommandOption[];
	onSelect: (option: ChatCommandOption) => void;
}

export function useSlashCommandAutocomplete(input: UseSlashCommandAutocompleteInput) {
	const [dismissedSignature, setDismissedSignature] = useState<string | null>(null);
	const [activeDescendantId, setActiveDescendantId] = useState<string | undefined>();
	const signature = slashInputSignature(input.content, input.selectionStart, input.selectionEnd);
	const query = getSlashCommandQuery(input);
	const matches = useMemo(() => query === null ? [] : matchSlashCommands(input.options, query), [input.options, query]);
	const opened = matches.length > 0 && signature !== dismissedSignature;
	const combobox = useCombobox({ opened });
	const { resetSelectedOption, selectFirstOption } = combobox;

	useEffect(() => {
		if (dismissedSignature && signature !== dismissedSignature) {
			setDismissedSignature(null);
		}
	}, [dismissedSignature, signature]);

	useEffect(() => {
		if (matches.length > 0) {
			selectFirstOption();
			setActiveDescendantId(slashCommandOptionId(matches[0]?.name ?? ""));
		} else {
			resetSelectedOption();
			setActiveDescendantId(undefined);
		}
	}, [matches, resetSelectedOption, selectFirstOption]);

	const dismiss = useCallback(() => {
		setDismissedSignature(signature);
		setActiveDescendantId(undefined);
		combobox.closeDropdown();
	}, [combobox, signature]);

	const select = useCallback((option: ChatCommandOption) => {
		input.onSelect(option);
		const canonical = `/${option.name}`;
		setDismissedSignature(slashInputSignature(canonical, canonical.length, canonical.length));
		setActiveDescendantId(undefined);
		combobox.closeDropdown();
	}, [combobox, input]);

	const onKeyDown = useCallback((event: React.KeyboardEvent<HTMLTextAreaElement>): boolean => {
		if (!opened || event.nativeEvent.isComposing || input.isComposing) {
			return false;
		}
		if (event.key === "Enter" && event.shiftKey) {
			// Mantine's target treats every coded Enter as option submission. Clearing selection lets its handler fall
			// through without selecting or preventing the textarea's normal Shift+Enter newline behavior.
			combobox.resetSelectedOption();
			setActiveDescendantId(undefined);
			return false;
		}
		if (event.key === "ArrowDown" || event.key === "ArrowUp") {
			const currentIndex = combobox.getSelectedOptionIndex();
			const nextIndex = event.key === "ArrowDown"
				? (currentIndex + 1) % matches.length
				: (currentIndex <= 0 ? matches.length : currentIndex) - 1;
			setActiveDescendantId(slashCommandOptionId(matches[nextIndex]?.name ?? ""));
			// Combobox.Target runs its own composed key handler after the textarea's handler. Returning handled keeps the
			// composer from sending while Mantine remains the single owner of selection and aria-activedescendant.
			return true;
		}
		if (event.key === "Escape") {
			event.preventDefault();
			dismiss();
			return true;
		}
		if (event.key === "Enter" && !event.shiftKey) {
			// Do not prevent or submit here: Mantine's target handler clicks the selected option after this child handler.
			return true;
		}
		if (event.key === "Tab") {
			event.preventDefault();
			combobox.clickSelectedOption();
			return true;
		}
		return false;
	}, [combobox, dismiss, input.isComposing, matches, opened]);

	return { activeDescendantId, combobox, dismiss, matches, onKeyDown, opened, select };
}
