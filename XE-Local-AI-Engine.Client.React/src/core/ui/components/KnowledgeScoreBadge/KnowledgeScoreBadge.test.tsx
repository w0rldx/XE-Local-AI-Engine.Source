// @vitest-environment jsdom

import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import i18next from "i18next";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { KnowledgeScoreBadge } from "@/core/ui/components/KnowledgeScoreBadge/KnowledgeScoreBadge";
import de from "@/locales/de.json";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

describe("KnowledgeScoreBadge", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(async () => {
		cleanup();
		// The app's real i18next instance is shared across the file, so a language switch must be undone.
		await i18next.changeLanguage("en");
	});

	it.each([
		{ scoreKind: "Rerank", score: 7.3312, kind: "Reranked", detail: "Cross-encoder relevance 7.33" },
		{ scoreKind: "Fusion", score: 0.03125, kind: "Hybrid match", detail: "Fusion score 0.031" },
	] as const)(
		"names the $scoreKind kind and carries the raw number as visually hidden text",
		({ scoreKind, score, kind, detail }) => {
			renderWithMantine(<KnowledgeScoreBadge score={score} scoreKind={scoreKind} />);

			const badge = screen.getByTestId("knowledge-score-kind");
			// Screen readers read the kind and the number; only the kind is visible.
			expect(badge.textContent).toBe(`${kind} ${detail}`);
			expect(within(badge).getByTestId("knowledge-score-detail").textContent).toBe(` ${detail}`);
		},
	);

	it("shows the same text in the hover tooltip", async () => {
		renderWithMantine(<KnowledgeScoreBadge score={7.3312} scoreKind="Rerank" />);

		fireEvent.mouseEnter(screen.getByTestId("knowledge-score-kind"));
		expect(await screen.findByRole("tooltip")).toHaveProperty("textContent", "Cross-encoder relevance 7.33");
	});

	it.each([
		{ scoreKind: "Rerank", score: 7.3312, expected: "Neu bewertet Cross-Encoder-Relevanz 7,33" },
		{ scoreKind: "Fusion", score: 0.03125, expected: "Hybrid-Treffer Fusionswert 0,031" },
	] as const)("renders the $scoreKind kind in German with a German decimal separator", async ({ scoreKind, score, expected }) => {
		i18next.addResourceBundle("de", "translation", de, true, true);
		await i18next.changeLanguage("de");
		renderWithMantine(<KnowledgeScoreBadge score={score} scoreKind={scoreKind} />);

		expect(screen.getByTestId("knowledge-score-kind").textContent).toBe(expected);
	});
});
