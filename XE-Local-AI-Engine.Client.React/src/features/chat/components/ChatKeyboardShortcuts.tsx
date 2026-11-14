import { Group, Kbd, Table } from "@mantine/core";
import { useEffect, useEffectEvent, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";

interface ChatKeyboardShortcutsProps {
	/** Stops the running generation; undefined while nothing runs, so Esc does nothing. */
	onStop?: () => void;
	/** Starts a new chat. */
	onNewChat?: () => void;
}

// Esc pressed inside one of these belongs to it (Mantine closes its own dialog, menu or listbox on Escape).
const POPUP_SELECTOR = '[role="dialog"], [role="alertdialog"], [role="menu"], [role="listbox"]';

// A Select, Combobox or Popover target keeps the focus while its dropdown is open; Mantine marks it so (its own modals
// skip Esc on the stop-propagation mark) but does not preventDefault.
const EXPANDED_TARGET_SELECTOR = '[aria-expanded="true"], [data-mantine-stop-propagation="true"]';

function isInsidePopup(target: EventTarget | null): boolean {
	return target instanceof Element && target.closest(POPUP_SELECTOR) !== null;
}

/**
 * The chat page's keyboard shortcuts and the list that names them. Mounted by the unscoped chat page only; Enter and
 * Shift+Enter stay with the composer.
 */
export function ChatKeyboardShortcuts({ onStop, onNewChat }: ChatKeyboardShortcutsProps) {
	const { t } = useTranslation();
	const [opened, setOpened] = useState(false);
	const mod =
		typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.userAgent) ? "⌘" : t("chat.shortcuts.keys.ctrl");

	const handleKeyDown = useEffectEvent((event: KeyboardEvent, ownedByExpandedTarget: boolean) => {
		// An IME composition owns its keys, and a key some other handler already took (the slash-command list's Esc) is not ours.
		if (event.defaultPrevented || event.isComposing || event.keyCode === 229) {
			return;
		}
		const modifier = event.ctrlKey || event.metaKey;
		if (event.key === "Escape") {
			if (onStop && !opened && !ownedByExpandedTarget && !isInsidePopup(event.target)) {
				event.preventDefault();
				onStop();
			}
			return;
		}
		if (modifier && event.shiftKey && event.key.toLowerCase() === "o") {
			if (onNewChat) {
				event.preventDefault();
				onNewChat();
			}
			return;
		}
		// No shiftKey check: on several layouts "/" itself needs Shift.
		if (modifier && event.key === "/") {
			event.preventDefault();
			setOpened((current) => !current);
		}
	});

	useEffect(() => {
		// Read in the capture phase: by the bubble phase the target's own handler may have closed it and React committed
		// that, so the mark would be gone.
		let expandedTargetEvent: Event | undefined;
		const capture = (event: KeyboardEvent) => {
			expandedTargetEvent = event.target instanceof Element && event.target.matches(EXPANDED_TARGET_SELECTOR) ? event : undefined;
		};
		const listener = (event: KeyboardEvent) => handleKeyDown(event, event === expandedTargetEvent);
		document.addEventListener("keydown", capture, true);
		document.addEventListener("keydown", listener);
		return () => {
			document.removeEventListener("keydown", capture, true);
			document.removeEventListener("keydown", listener);
		};
	}, []);

	const shift = t("chat.shortcuts.keys.shift");
	const enter = t("chat.shortcuts.keys.enter");
	const rows: { keys: string[]; label: string }[] = [
		{ keys: [enter], label: t("chat.shortcuts.send") },
		{ keys: [shift, enter], label: t("chat.shortcuts.newLine") },
		{ keys: [t("chat.shortcuts.keys.esc")], label: t("chat.shortcuts.stop") },
		{ keys: [mod, shift, "O"], label: t("chat.shortcuts.newChat") },
		{ keys: [mod, "/"], label: t("chat.shortcuts.showList") },
		// App-wide (CommandPalette), listed here because this is the one shortcut list.
		{ keys: [mod, "K"], label: t("commandPalette.shortcut") },
	];

	return (
		<DialogShell
			opened={opened}
			onClose={() => setOpened(false)}
			title={t("chat.shortcuts.title")}
			enableFullScreenToggle={false}
			size="sm"
			data-testid="chat-shortcuts-dialog"
		>
			<Table>
				<Table.Tbody>
					{rows.map((row) => (
						<Table.Tr key={row.label}>
							<Table.Td>
								<Group gap={4} wrap="nowrap">
									{row.keys.map((key) => (
										<Kbd key={key}>{key}</Kbd>
									))}
								</Group>
							</Table.Td>
							<Table.Td>{row.label}</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</DialogShell>
	);
}
