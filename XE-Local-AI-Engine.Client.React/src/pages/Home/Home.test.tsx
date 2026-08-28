// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { Home } from "@/pages/Home/Home";

// The landing page's whole job is to name the next action, so what it must never do is tell a node that CAN answer
// that it cannot. An external-only node — no GGUF installed, one registered OpenAI-compatible connection — is a
// supported shape, and it was being sent to /models with the chat route hidden.

// The card renders TanStack-router Links; stub the router so Home mounts without a RouterProvider.
vi.mock("@tanstack/react-router", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@tanstack/react-router")>();
	return {
		...actual,
		Link: ({ children, to, ...props }: { children: ReactNode; to: string; [key: string]: unknown }) => (
			<a href={to} {...props}>
				{children}
			</a>
		),
	};
});

const { listLocalModelsQueryFn } = vi.hoisted(() => ({
	listLocalModelsQueryFn: vi.fn(),
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	listLocalModelsOptions: vi.fn(() => ({
		// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
		queryKey: [{ _id: "listLocalModels" }],
		queryFn: () => listLocalModelsQueryFn(),
	})),
}));

function renderHome(): void {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
	render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider>
				<Home />
			</MantineProvider>
		</QueryClientProvider>,
	);
}

describe("Home landing card", () => {
	beforeEach(() => {
		// MantineProvider reads the color scheme through matchMedia, which jsdom does not implement.
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
		vi.clearAllMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("offers chat on a node whose only chat model is an external registration", async () => {
		listLocalModelsQueryFn.mockResolvedValue({
			items: [
				{
					modelName: "ext:unsloth-box/qwen3",
					provider: "external",
					kind: "Chat",
					externalConnectionId: "unsloth-box",
					externalConnectionName: "Unsloth box",
					declaredLocality: "local",
				},
			],
			isAvailable: true,
		});

		renderHome();

		expect(await screen.findByTestId("home-go-to-chat")).toBeTruthy();
		expect((await screen.findByTestId("home-status")).textContent).toContain("external provider");
	});

	it("withholds chat on a node with no local models and no external registration", async () => {
		listLocalModelsQueryFn.mockResolvedValue({ items: [], isAvailable: true });

		renderHome();

		await waitFor(() => {
			expect(screen.getByTestId("home-status").textContent).toContain("cannot answer anything");
		});
		expect(screen.queryByTestId("home-go-to-chat")).toBeNull();
	});

	it("does not count a non-chat external registration as a usable send path", async () => {
		listLocalModelsQueryFn.mockResolvedValue({
			items: [{ modelName: "ext:unsloth-box/bge-m3", provider: "external", kind: "Embedding", externalConnectionId: "unsloth-box" }],
			isAvailable: true,
		});

		renderHome();

		await waitFor(() => {
			expect(screen.getByTestId("home-status").textContent).toContain("cannot answer anything");
		});
		expect(screen.queryByTestId("home-go-to-chat")).toBeNull();
	});

	it("reports the installed count when local models are present", async () => {
		listLocalModelsQueryFn.mockResolvedValue({
			items: [{ modelName: "qwen3-27b.gguf", provider: "LlamaServer", kind: "Chat" }],
			isAvailable: true,
		});

		renderHome();

		expect((await screen.findByTestId("home-status")).textContent).toContain("local model(s) installed");
		expect(screen.getByTestId("home-go-to-chat")).toBeTruthy();
	});
});
