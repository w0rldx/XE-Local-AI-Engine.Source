import { Button, Stack, Text, TextInput } from "@mantine/core";
import { IconSearch } from "@tabler/icons-react";
import { type RefObject, useDeferredValue, useLayoutEffect, useState } from "react";
import { useTranslation } from "react-i18next";

// The page's results view marks each section with this attribute (holding its label) and its cards' parent with the second.
const SEARCH_SECTION_ATTRIBUTE = "data-search-section";
const SEARCH_CARDS_ATTRIBUTE = "data-search-cards";

const HIGHLIGHT_NAME = "node-settings-search";
const FOCUSABLE =
	"input:not([type='hidden']):not(:disabled), textarea:not(:disabled), select:not(:disabled), button:not(:disabled)";

interface Props {
	// The results view: every listed section, rendered while a query is entered.
	readonly resultsRef: RefObject<HTMLElement | null>;
	readonly onSearchingChange: (searching: boolean) => void;
	// Present in Simple UI mode while the advanced sections are hidden: they are not searched until shown.
	readonly onShowAdvanced?: () => void;
}

function sectionCards(results: HTMLElement): HTMLElement[] {
	return [...results.querySelectorAll<HTMLElement>(`[${SEARCH_CARDS_ATTRIBUTE}] > *`)];
}

function collectRanges(card: HTMLElement, query: string, ranges: Range[]): void {
	const walker = document.createTreeWalker(card, NodeFilter.SHOW_TEXT);
	for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
		const original = node.textContent ?? "";
		const text = original.toLowerCase();
		// Lower-casing can change a string's length (a few non-Latin letters); offsets would then point elsewhere.
		if (text.length !== original.length) {
			continue;
		}
		for (let at = text.indexOf(query); at !== -1; at = text.indexOf(query, at + query.length)) {
			const range = new Range();
			range.setStart(node, at);
			range.setEnd(node, at + query.length);
			ranges.push(range);
		}
	}
}

// Filters what the results view RENDERED, so a card added to any section is searchable by its title, labels and
// descriptions with no list to keep in step. A mutation observer re-applies it when a card's own content arrives later.
function useSearchFilter(resultsRef: RefObject<HTMLElement | null>, query: string): number {
	const [matchCount, setMatchCount] = useState(0);

	useLayoutEffect(() => {
		const results = resultsRef.current;
		if (results === null || query === "") {
			return;
		}
		const highlights = typeof CSS !== "undefined" && "highlights" in CSS ? CSS.highlights : undefined;
		// The page keeps its cards mounted after a search, so every element this filter hid is shown again on cleanup.
		const touched = new Set<HTMLElement>();
		const apply = (): void => {
			const ranges: Range[] = [];
			let count = 0;
			for (const section of results.querySelectorAll<HTMLElement>(`[${SEARCH_SECTION_ATTRIBUTE}]`)) {
				const sectionMatches = (section.getAttribute(SEARCH_SECTION_ATTRIBUTE) ?? "").toLowerCase().includes(query);
				let shown = 0;
				touched.add(section);
				for (const card of sectionCards(section)) {
					const matches = sectionMatches || (card.textContent ?? "").toLowerCase().includes(query);
					touched.add(card);
					card.style.display = matches ? "" : "none";
					if (matches) {
						shown += 1;
						if (highlights !== undefined) {
							collectRanges(card, query, ranges);
						}
					}
				}
				section.style.display = shown > 0 ? "" : "none";
				count += shown;
			}
			setMatchCount(count);
			highlights?.set(HIGHLIGHT_NAME, new Highlight(...ranges));
		};
		apply();
		// Attributes are not observed: apply() writes styles, which must not re-trigger it.
		const observer = new MutationObserver(apply);
		observer.observe(results, { childList: true, subtree: true, characterData: true });
		return () => {
			observer.disconnect();
			highlights?.delete(HIGHLIGHT_NAME);
			for (const element of touched) {
				element.style.display = "";
			}
		};
	}, [resultsRef, query]);

	return matchCount;
}

// The search box above the section list. It owns the query so typing re-renders this box, not the whole page; the page
// only swaps to its all-sections results view while `searching`.
export function NodeSettingsSearch({ resultsRef, onSearchingChange, onShowAdvanced }: Props) {
	const { t } = useTranslation();
	const [value, setValue] = useState("");
	const query = useDeferredValue(value.trim().toLowerCase());
	const matchCount = useSearchFilter(resultsRef, query);

	const change = (next: string): void => {
		setValue(next);
		onSearchingChange(next.trim() !== "");
	};

	const focusFirstMatch = (): void => {
		const results = resultsRef.current;
		const card = results === null ? undefined : sectionCards(results).find((candidate) => candidate.style.display !== "none");
		card?.querySelector<HTMLElement>(FOCUSABLE)?.focus();
	};

	let status = "";
	if (query !== "") {
		status =
			matchCount > 0
				? t("pages.nodeSettings.search.matches", "{{count}} matching cards", { count: matchCount })
				: t("pages.nodeSettings.search.noMatches", "No settings match “{{query}}”.", { query: value.trim() });
	}

	return (
		<Stack gap={6} mb="sm" role="search">
			<TextInput
				type="search"
				aria-label={t("pages.nodeSettings.search.label", "Search settings")}
				placeholder={t("pages.nodeSettings.search.placeholder", "Search settings")}
				leftSection={<IconSearch size={16} />}
				value={value}
				onChange={(event) => change(event.currentTarget.value)}
				onKeyDown={(event) => {
					if (event.key === "Escape" && value !== "") {
						event.preventDefault();
						change("");
					} else if (event.key === "Enter") {
						event.preventDefault();
						focusFirstMatch();
					}
				}}
				data-testid="node-settings-search"
			/>
			{/* Always mounted: a live region inserted together with its text is not reliably announced. */}
			<Text size="sm" c="dimmed" role="status" data-testid="node-settings-search-status">
				{status}
			</Text>
			{query !== "" && matchCount === 0 && onShowAdvanced ? (
				<Button variant="subtle" size="xs" onClick={onShowAdvanced} data-testid="node-settings-search-show-advanced">
					{t("pages.nodeSettings.search.searchAdvanced", "Search the advanced sections too")}
				</Button>
			) : null}
		</Stack>
	);
}
