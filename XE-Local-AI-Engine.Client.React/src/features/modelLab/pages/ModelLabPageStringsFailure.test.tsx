// @vitest-environment jsdom

// A strings chunk that will not load: a browser can keep answering a failed import from its cache.

import { act, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { ModelLabPage } from "@/features/modelLab/pages/ModelLabPage";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const attempts = vi.hoisted(() => ({ count: 0 }));

vi.mock("@/features/modelLab/locales/model-lab-en.json", () => {
	attempts.count += 1;
	throw new Error("Failed to fetch dynamically imported module");
});
vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: () => ({
		connection: { state: "Disconnected", on: vi.fn(), off: vi.fn(), invoke: vi.fn() },
		whenStarted: Promise.resolve(),
		onReconnected: vi.fn(),
		onReconnecting: vi.fn(),
		onClosed: vi.fn(),
		release: vi.fn(),
	}),
}));
vi.mock("@/features/modelLab/components/TraceExperiments", () => ({ TraceExperiments: () => null }));

setupMswServer();

describe("ModelLabPage when its strings will not load", () => {
	it("renders its English defaults instead of suspending forever", async () => {
		server.use(http.get(localApiPath("models"), () => HttpResponse.json({ items: [] })));

		await act(async () => {
			renderWithProviders(<ModelLabPage />);
		});

		await waitFor(() => expect(screen.getByRole("tab", { name: "Trace and steer" })).toBeTruthy(), { timeout: 5000 });
		expect(attempts.count).toBe(1);
	});
});
