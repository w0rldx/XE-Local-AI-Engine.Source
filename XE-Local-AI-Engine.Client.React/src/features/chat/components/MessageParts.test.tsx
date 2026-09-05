// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { MessageParts } from "@/features/chat/components/MessageParts";
import type { ChatMessagePart } from "@/features/chat/models/ChatModels";
import { jsonRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

// The rendering contract under test (docs/agent-knowledge.md §5 "Chat rendering contract"): an assistant turn is ONE
// ordered `parts[]` array — reasoning ↔ tool ↔ reasoning → answer — not fixed sections. The renderer must emit the
// parts in array order regardless of kind, and only the TRAILING reasoning run of a live turn may stream; every
// earlier reasoning run is already complete and renders as a folded Thoughts block.

// ToolCallCard resolves each tool's risk class from the shared catalog query. Serve an empty catalog over MSW rather
// than mocking the hook: the card's fail-closed "Unknown" fallback is then exercised for real, and no request escapes.
const emptyToolCatalog = jsonRoute("get", "tool-catalog", { tools: [] });

function reasoning(id: string, sequence: number, text: string): ChatMessagePart {
	return { kind: "reasoning", id, sequence, text };
}

function tool(id: string, sequence: number, name: string): ChatMessagePart {
	return { kind: "tool", id, sequence, name, state: "received", result: "ok" };
}

function text(id: string, sequence: number, value: string): ChatMessagePart {
	return { kind: "text", id, sequence, text: value };
}

function notice(id: string, sequence: number, value: string): ChatMessagePart {
	return { kind: "notice", id, sequence, noticeKind: "ModelSubstituted", text: value };
}

/** The single DOM element that stands for a rendered part, so ordering can be asserted across mixed kinds. */
function markerFor(part: ChatMessagePart): HTMLElement {
	switch (part.kind) {
		case "reasoning":
			return screen.getByTestId(`chat-message-reasoning-${part.id}`);
		case "tool":
			return screen.getByTestId(`chat-tool-call-card-${part.name}`);
		case "notice":
			return screen.getByText(part.text);
		default:
			return screen.getByText(part.text);
	}
}

/** Fails unless every element precedes the next one in document order. */
function expectDocumentOrder(elements: HTMLElement[]): void {
	for (let index = 0; index < elements.length - 1; index += 1) {
		const current = elements[index] as HTMLElement;
		const next = elements[index + 1] as HTMLElement;
		const following = (current.compareDocumentPosition(next) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
		// biome-ignore lint/suspicious/noMisplacedAssertion: the whole point of `expectDocumentOrder` is to assert for its callers, which are tests.
		expect(following, `part ${index} must render before part ${index + 1}`).toBe(true);
	}
}

describe("MessageParts", () => {
	beforeEach(() => {
		server.use(emptyToolCatalog);
	});

	afterEach(cleanup);

	it("renders nothing for an empty parts array", () => {
		const { container } = renderWithProviders(<MessageParts parts={[]} />);

		// MantineProvider injects its own <style> tags into the container, so "rendered nothing" means no element
		// of its own — not an empty innerHTML.
		expect(container.querySelectorAll(":scope > :not(style)")).toHaveLength(0);
	});

	it("preserves array order across reasoning, tool, text and notice parts", async () => {
		const parts: ChatMessagePart[] = [
			reasoning("m1:0", 0, "First I check the clock."),
			tool("call-1", 1, "get_time"),
			reasoning("m1:2", 2, "Now I know the time."),
			notice("m1:3", 3, "Switched to a smaller model."),
			text("m1:4", 4, "It is noon."),
		];

		renderWithProviders(<MessageParts parts={parts} />);

		expectDocumentOrder(parts.map(markerFor));
		expect(await screen.findByTestId("chat-tool-call-card-get_time")).toBeTruthy();
	});

	// The same array rendered back-to-front must render back-to-front: the order comes from `parts`, not from a
	// per-kind bucket. This is the assertion that fails if anyone reintroduces fixed reasoning/tool/answer sections.
	it("follows the array when a tool precedes all reasoning", () => {
		const parts: ChatMessagePart[] = [
			tool("call-1", 0, "get_time"),
			reasoning("m2:1", 1, "The tool answered."),
			text("m2:2", 2, "Done."),
		];

		renderWithProviders(<MessageParts parts={parts} />);

		expectDocumentOrder(parts.map(markerFor));
	});

	it("streams only the trailing reasoning run and folds the earlier one", () => {
		const parts: ChatMessagePart[] = [
			reasoning("m3:0", 0, "Completed thought before the tool."),
			tool("call-1", 1, "get_time"),
			reasoning("m3:2", 2, "Still thinking abo"),
		];

		renderWithProviders(<MessageParts parts={parts} isStreaming={true} />);

		// The trailing run renders as the live stream (partial text included), the earlier run as a folded block.
		const live = screen.getByTestId("chat-live-reasoning-stream");
		expect(live.textContent).toContain("Still thinking abo");
		expect(screen.getByTestId("chat-message-reasoning-m3:0")).toBeTruthy();
		expect(screen.queryByTestId("chat-message-reasoning-m3:2")).toBeNull();
		expectDocumentOrder([screen.getByTestId("chat-message-reasoning-m3:0"), screen.getByTestId("chat-tool-call-card-get_time"), live]);
	});

	it("folds every reasoning run once the turn is no longer streaming", () => {
		const parts: ChatMessagePart[] = [reasoning("m4:0", 0, "One."), tool("call-1", 1, "get_time"), reasoning("m4:2", 2, "Two.")];

		renderWithProviders(<MessageParts parts={parts} isStreaming={false} />);

		expect(screen.queryByTestId("chat-live-reasoning-stream")).toBeNull();
		expect(screen.getByTestId("chat-message-reasoning-m4:0")).toBeTruthy();
		expect(screen.getByTestId("chat-message-reasoning-m4:2")).toBeTruthy();
	});

	// The overflow counter belongs to the live segment only; a folded earlier run must not claim truncated bytes.
	it("attributes the streaming overflow byte count to the live segment", () => {
		const parts: ChatMessagePart[] = [reasoning("m5:0", 0, "Earlier."), reasoning("m5:1", 1, "Live.")];

		renderWithProviders(<MessageParts parts={parts} isStreaming={true} streamingReasoningOverflowBytes={2048} />);

		expect(screen.getByTestId("chat-live-reasoning-stream").textContent).toContain("+2 KB");
	});
});
