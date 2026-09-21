// @vitest-environment jsdom

// The WIRE-CONTRACT guard for `knowledge-base/hub`. The optimistic cache write is gated on `typeof status === "string"`,
// so a NUMBER — what SignalR emits for a CLR enum without the app's JSON options — skips it with no error or warning.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { KnowledgeDocument } from "@/features/knowledge/models/KnowledgeModels";
import { knowledgeInvalidationKey, knowledgeQueryIds } from "@/features/knowledge/queries/useKnowledgeDocuments";

const hubMock = vi.hoisted(() => {
	const handlers = new Map<string, (event: unknown) => void>();
	const connection = {
		state: "Connected",
		on: vi.fn((event: string, handler: (event: unknown) => void) => handlers.set(event, handler)),
		off: vi.fn((event: string) => handlers.delete(event)),
		invoke: vi.fn(async (): Promise<unknown> => undefined),
	};
	const handle = { connection, whenStarted: Promise.resolve(), onReconnected: vi.fn(), release: vi.fn() };
	return { acquire: vi.fn(() => handle), connection, handlers };
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: hubMock.acquire,
}));

import { useKnowledgeBaseHub } from "@/features/knowledge/hooks/useKnowledgeBaseHub";

const documentId = "11111111-1111-4111-8111-111111111111";
const listKey = knowledgeInvalidationKey(knowledgeQueryIds.listDocuments);

function cachedRow(status: string): { items: KnowledgeDocument[] } {
	return { items: [{ documentId, status } as unknown as KnowledgeDocument] };
}

function harness(): { queryClient: QueryClient; wrapper: ({ children }: { children: ReactNode }) => ReactNode } {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	queryClient.setQueryData(listKey, cachedRow("Extracting"));
	return {
		queryClient,
		wrapper: ({ children }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>,
	};
}

function cachedStatus(queryClient: QueryClient): string | undefined {
	const cached = queryClient.getQueryData<{ items?: readonly KnowledgeDocument[] }>(listKey);
	return cached?.items?.[0]?.status;
}

function emit(event: unknown): void {
	const handler = hubMock.handlers.get("knowledge.documentChanged");
	if (!handler) {
		throw new Error("the hook did not subscribe to knowledge.documentChanged");
	}
	act(() => handler(event));
}

describe("useKnowledgeBaseHub", () => {
	beforeEach(() => {
		hubMock.handlers.clear();
	});

	it("stamps a pushed enum-name status onto the cached row", () => {
		const { queryClient, wrapper } = harness();
		renderHook(() => useKnowledgeBaseHub(), { wrapper });

		emit({ documentId, status: "Indexed" });

		expect(cachedStatus(queryClient)).toBe("Indexed");
	});

	it("leaves the cached row alone when the status arrives as a raw enum number", () => {
		// The pre-fix wire shape. Writing a number into the row would render an unlabelled pill, so the guard is right to
		// skip it — the fix is the server sending the name, which the test above pins.
		const { queryClient, wrapper } = harness();
		renderHook(() => useKnowledgeBaseHub(), { wrapper });

		emit({ documentId, status: 4 });

		expect(cachedStatus(queryClient)).toBe("Extracting");
	});

	it("subscribes to the knowledge-base hub and releases its lease on unmount", () => {
		const { wrapper } = harness();
		const view = renderHook(() => useKnowledgeBaseHub(), { wrapper });

		expect(hubMock.acquire).toHaveBeenCalledWith("knowledge-base/hub");

		view.unmount();

		expect(hubMock.connection.off).toHaveBeenCalledWith("knowledge.documentChanged", expect.any(Function));
	});
});
