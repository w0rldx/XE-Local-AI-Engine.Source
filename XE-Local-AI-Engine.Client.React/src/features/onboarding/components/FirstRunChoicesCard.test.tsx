// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { getNodeSettingsQueryKey, getTutorialStateQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { nodeChatQueryKeys } from "@/core/chat/queries/NodeChatQueryKeys";
import { FirstRunChoicesCard } from "@/features/onboarding/components/FirstRunChoicesCard";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const choicesKey = "first-run-choices-v1";

// The node and the per-user tutorial state live behind MSW, so "remembered" is what the server stored, not a mock call.
let storedEntries: { key: string; status: string }[];
let settings: Record<string, unknown>;
let savedSettingsBody: unknown;

function serveNode(): void {
	const entriesResponse = () =>
		HttpResponse.json({ entries: storedEntries.map((entry) => ({ ...entry, atUtc: "2026-11-13T00:00:00Z" })) });
	server.use(
		http.get(localApiPath("node-settings"), () => HttpResponse.json(settings)),
		http.put(localApiPath("node-settings"), async ({ request }) => {
			savedSettingsBody = await request.json();
			settings = { ...settings, ...(savedSettingsBody as Record<string, unknown>) };
			return HttpResponse.json(settings);
		}),
		http.get(localApiPath("tutorial-state"), entriesResponse),
		http.put(localApiPath("tutorial-state"), async ({ request }) => {
			const entry = (await request.json()) as { key: string; status: string };
			storedEntries = [...storedEntries.filter((existing) => existing.key !== entry.key), entry];
			return entriesResponse();
		}),
	);
}

// A real gcTime: the seeded conversation has no observer, and the suite's default `gcTime: 0` would evict it at once.
function client(): QueryClient {
	return new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
}

function answer(queryClient: QueryClient): void {
	act(() => {
		queryClient.setQueryData(nodeChatQueryKeys.conversation("c1"), {
			messages: [
				{ role: "user", content: "hello" },
				{ role: "assistant", content: "Hi there." },
			],
		});
	});
}

async function settled(queryClient: QueryClient): Promise<void> {
	await waitFor(() => {
		expect(queryClient.getQueryData(getNodeSettingsQueryKey())).toBeDefined();
		expect(queryClient.getQueryData(getTutorialStateQueryKey())).toBeDefined();
	});
}

describe("FirstRunChoicesCard", () => {
	beforeEach(() => {
		storedEntries = [];
		settings = { externalAccessProfile: "recommended", sandboxSecurityProfile: "pending", uiMode: "simple" };
		savedSettingsBody = undefined;
		useNodeAuthStore.getState().actions.setToken({ accessToken: "access-token", expiresAtUtc: "2099-01-01T00:00:00Z" });
		serveNode();
	});

	afterEach(() => cleanup());

	it("appears only after the first answer, offering the two defaults", async () => {
		const queryClient = client();
		renderWithProviders(<FirstRunChoicesCard idle={true} />, { queryClient });
		await settled(queryClient);
		expect(screen.queryByTestId("first-run-choices-card")).toBeNull();

		answer(queryClient);

		const card = await screen.findByTestId("first-run-choices-card");
		expect(card.textContent).toContain("Two settings you can change");
		expect((screen.getByLabelText("Simple") as HTMLInputElement).checked).toBe(true);
		expect((screen.getByLabelText("Low") as HTMLInputElement).checked).toBe(true);
	});

	it("offers Advanced, and says so, to a node upgraded part-way through first run with no interface mode", async () => {
		// useUiMode renders a null mode as Advanced, so preselecting Simple would switch the operator to a mode they never had.
		settings = { ...settings, uiMode: null };
		const queryClient = client();
		renderWithProviders(<FirstRunChoicesCard idle={true} />, { queryClient });
		answer(queryClient);

		const card = await screen.findByTestId("first-run-choices-card");
		expect((screen.getByLabelText("Advanced") as HTMLInputElement).checked).toBe(true);
		expect(card.textContent).toContain("runs in Advanced mode");
		expect(card.textContent).not.toContain("Simple mode");

		fireEvent.click(screen.getByTestId("first-run-choices-save"));
		await waitFor(() => expect(savedSettingsBody).toEqual({ uiMode: "advanced", sandboxSecurityProfile: "low" }));
	});

	it("stays hidden while another overlay is open", async () => {
		const queryClient = client();
		renderWithProviders(<FirstRunChoicesCard idle={false} />, { queryClient });
		answer(queryClient);
		await settled(queryClient);

		expect(screen.queryByTestId("first-run-choices-card")).toBeNull();
	});

	it("is never offered to a node that already chose its sandbox profile", async () => {
		settings = { ...settings, sandboxSecurityProfile: "low", uiMode: "advanced" };
		const queryClient = client();
		renderWithProviders(<FirstRunChoicesCard idle={true} />, { queryClient });
		answer(queryClient);
		await settled(queryClient);

		expect(screen.queryByTestId("first-run-choices-card")).toBeNull();
	});

	it("records a dismissal and does not come back on the next visit", async () => {
		const first = client();
		renderWithProviders(<FirstRunChoicesCard idle={true} />, { queryClient: first });
		answer(first);
		fireEvent.click(await screen.findByTestId("first-run-choices-not-now"));

		expect(screen.queryByTestId("first-run-choices-card")).toBeNull();
		await waitFor(() => expect(storedEntries).toEqual([{ key: choicesKey, status: "skipped" }]));
		expect(savedSettingsBody).toBeUndefined();
		cleanup();

		const second = client();
		renderWithProviders(<FirstRunChoicesCard idle={true} />, { queryClient: second });
		answer(second);
		await settled(second);
		expect(screen.queryByTestId("first-run-choices-card")).toBeNull();
	});

	it("saves both choices explicitly and records them", async () => {
		const queryClient = client();
		renderWithProviders(<FirstRunChoicesCard idle={true} />, { queryClient });
		answer(queryClient);
		await screen.findByTestId("first-run-choices-card");

		fireEvent.click(screen.getByLabelText("Advanced"));
		fireEvent.click(screen.getByLabelText("High"));
		expect(screen.getByTestId("first-run-choices-card").textContent).toContain("Node settings lists which ones.");
		fireEvent.click(screen.getByTestId("first-run-choices-save"));

		await waitFor(() => expect(savedSettingsBody).toEqual({ uiMode: "advanced", sandboxSecurityProfile: "high" }));
		await waitFor(() => expect(storedEntries).toEqual([{ key: choicesKey, status: "completed" }]));
		expect(screen.queryByTestId("first-run-choices-card")).toBeNull();
		expect(queryClient.getQueryData(getNodeSettingsQueryKey())).toMatchObject({ uiMode: "advanced" });
	});
});
