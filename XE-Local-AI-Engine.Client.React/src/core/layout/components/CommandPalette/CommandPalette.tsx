import { Box, Text, TextInput } from "@mantine/core";
import { useHotkeys } from "@mantine/hooks";
import { IconSearch } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useId, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { useUiMode } from "@/core/layout/hooks/useUiMode";
import {
	filterNavigationLinksByDisabledCapabilities,
	filterNavigationLinksByUiMode,
	navigationLinks,
	useServerDisabledNavigationCapabilities,
} from "@/core/layout/models/NavigationMenuData";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";

interface PalettePage {
	readonly to: string;
	readonly label: string;
	// The nav group the page sits in, shown beside it and searched with it ("Settings Node Settings").
	readonly group?: string;
}

// The pages the navigation shows right now: the same capability, feature-switch and UI-mode filters the nav bars apply.
function usePalettePages(): PalettePage[] {
	const { t } = useTranslation();
	const uiMode = useUiMode();
	const serverDisabled = useServerDisabledNavigationCapabilities();
	return useMemo(
		() =>
			filterNavigationLinksByUiMode(filterNavigationLinksByDisabledCapabilities(navigationLinks, serverDisabled), uiMode).flatMap(
				(link): PalettePage[] =>
					link.links
						? link.links.map((nested) => ({ to: nested.to, label: t(nested.translationKey), group: t(link.translationKey) }))
						: link.to
							? [{ to: link.to, label: t(link.translationKey) }]
							: [],
			),
		[t, uiMode, serverDisabled],
	);
}

function PageSwitcher({ onClose }: { readonly onClose: () => void }) {
	const { t } = useTranslation();
	const navigate = useNavigate();
	const pages = usePalettePages();
	const listId = useId();
	const [query, setQuery] = useState("");
	const [active, setActive] = useState(0);

	// Every typed word must appear in the page's label or group, in any order.
	const words = query.toLowerCase().split(/\s+/).filter(Boolean);
	const matches = pages.filter((page) => {
		const text = `${page.group ?? ""} ${page.label}`.toLowerCase();
		return words.every((word) => text.includes(word));
	});
	const activeIndex = Math.min(active, matches.length - 1);
	const optionId = (index: number): string => `${listId}-option-${index}`;

	const open = (page: PalettePage | undefined): void => {
		if (page !== undefined) {
			onClose();
			navigate({ to: page.to });
		}
	};

	return (
		<>
			<TextInput
				data-autofocus={true}
				role="combobox"
				aria-label={t("commandPalette.inputLabel")}
				aria-expanded={matches.length > 0}
				aria-controls={listId}
				aria-autocomplete="list"
				aria-activedescendant={matches.length > 0 ? optionId(activeIndex) : undefined}
				placeholder={t("commandPalette.placeholder")}
				leftSection={<IconSearch size={16} />}
				value={query}
				onChange={(event) => {
					setQuery(event.currentTarget.value);
					setActive(0);
				}}
				onKeyDown={(event) => {
					if (event.nativeEvent.isComposing || matches.length === 0) {
						return;
					}
					if (event.key === "ArrowDown" || event.key === "ArrowUp") {
						event.preventDefault();
						const step = event.key === "ArrowDown" ? 1 : -1;
						setActive((activeIndex + step + matches.length) % matches.length);
					} else if (event.key === "Enter") {
						event.preventDefault();
						open(matches[activeIndex]);
					}
				}}
				data-testid="command-palette-input"
			/>
			{matches.length === 0 ? (
				<Text c="dimmed" size="sm" mt="sm" role="status" data-testid="command-palette-empty">
					{t("commandPalette.noMatches")}
				</Text>
			) : null}
			{/* Focus stays in the input (aria-activedescendant names the active option), so options take no focus of their
			    own; a pointer click still picks one. */}
			<Box component="ul" id={listId} role="listbox" aria-label={t("commandPalette.listLabel")} p={0} mt="sm" mb={0}>
				{matches.map((page, index) => (
					<Box
						component="li"
						key={page.to}
						id={optionId(index)}
						role="option"
						aria-selected={index === activeIndex}
						onClick={() => open(page)}
						onMouseMove={() => setActive(index)}
						px="sm"
						py={6}
						bg={index === activeIndex ? "var(--mantine-primary-color-light)" : undefined}
						style={{ listStyle: "none", cursor: "pointer", borderRadius: "var(--mantine-radius-sm)" }}
						data-testid="command-palette-option"
					>
						<Text size="sm" span={true}>
							{page.label}
						</Text>
						{page.group ? (
							<Text size="xs" c="dimmed" span={true} ml="xs">
								{page.group}
							</Text>
						) : null}
					</Box>
				))}
			</Box>
		</>
	);
}

// Ctrl+K (Cmd+K on macOS) anywhere in the app opens a page switcher over the navigation's own entries. Mounted once by
// Layout. The key also works inside text fields (the chat composer gives it no meaning) but not inside a code editor,
// where it starts the editor's own chords, and not over another dialog.
export function CommandPalette() {
	const { t } = useTranslation();
	const [opened, setOpened] = useState(false);

	useHotkeys(
		[
			[
				"mod+K",
				(event) => {
					const target = event.target instanceof Element ? event.target : null;
					if (event.defaultPrevented || event.isComposing || target?.closest(".monaco-editor")) {
						return;
					}
					if (!opened && target?.closest('[role="dialog"], [role="alertdialog"]')) {
						return;
					}
					event.preventDefault();
					setOpened((current) => !current);
				},
				{ preventDefault: false },
			],
		],
		[],
		true,
	);

	return (
		<DialogShell
			opened={opened}
			onClose={() => setOpened(false)}
			title={t("commandPalette.title")}
			enableFullScreenToggle={false}
			size="md"
			yOffset="10dvh"
			centered={false}
			data-testid="command-palette"
		>
			{opened ? <PageSwitcher onClose={() => setOpened(false)} /> : null}
		</DialogShell>
	);
}
