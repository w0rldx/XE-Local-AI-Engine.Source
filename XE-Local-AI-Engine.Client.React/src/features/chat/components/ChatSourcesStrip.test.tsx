// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ChatSourcesStrip } from "@/features/chat/components/ChatSourcesStrip";
import type { ChatMessageSource } from "@/features/chat/models/ChatModels";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

function source(overrides: Partial<ChatMessageSource> = {}): ChatMessageSource {
	return {
		documentId: "11111111-1111-1111-1111-111111111111",
		chunkId: "22222222-2222-2222-2222-222222222222",
		title: "Design Doc",
		section: "Overview",
		score: 0.87,
		...overrides,
	};
}

describe("ChatSourcesStrip", () => {
	beforeEach(() => {
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				onchange: null,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("renders a card per source with title and section", () => {
		renderWithProviders(
			<ChatSourcesStrip
				sources={[source({ title: "Alpha", section: "Intro" }), source({ chunkId: "33", title: "Beta", section: undefined })]}
			/>,
		);

		expect(screen.getByTestId("chat-sources-strip")).toBeTruthy();
		expect(screen.getAllByTestId("chat-source-card")).toHaveLength(2);
		expect(screen.getByText("Alpha")).toBeTruthy();
		expect(screen.getByText("Intro")).toBeTruthy();
		expect(screen.getByText("Beta")).toBeTruthy();
	});

	it("starts collapsed and toggles open on click", () => {
		renderWithProviders(<ChatSourcesStrip sources={[source()]} />);

		const toggle = screen.getByTestId("chat-sources-toggle");
		expect(toggle.getAttribute("aria-expanded")).toBe("false");

		fireEvent.click(toggle);
		expect(toggle.getAttribute("aria-expanded")).toBe("true");
	});

	it("renders nothing when there are no sources", () => {
		renderWithProviders(<ChatSourcesStrip sources={[]} />);

		expect(screen.queryByTestId("chat-sources-strip")).toBeNull();
	});
});
