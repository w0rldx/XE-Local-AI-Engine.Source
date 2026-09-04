// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { WorkSessionsPage } from "@/features/workSessions/pages/WorkSessionsPage";
import { jsonRoute, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const navigate = vi.hoisted(() => vi.fn());

// The app router is built from routeTree.gen.ts; a unit test only needs the navigate CALL, not a real route match.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigate,
}));

const sessionId = "aaaaaaaa-0000-4000-8000-000000000001";
const agentId = "bbbbbbbb-0000-4000-8000-000000000002";

function agentsRoute() {
	return jsonRoute("get", "agents", {
		items: [
			{
				id: agentId,
				name: "Work Session — Research",
				description: "Research persona",
				instructions: "research",
				modelProfile: null,
				reasoningEffort: null,
				kind: "Single",
				allowedToolNames: [],
				toolApprovals: {},
				allowedSkillIds: [],
				orchestrationTopologyJson: null,
				playbookEnabled: false,
				defaultTemporaryChat: false,
				memoryExtractionEnabled: true,
				disableBaseScaffold: false,
				// Required (non-nullable) on AgentDefinitionResponse: the list response is zod-validated at the API
				// boundary, so an omitted required field fails the whole query and the selector renders no agents.
				disableToolRelevanceFilter: false,
				version: 1,
				createdAtUtc: 1,
				updatedAtUtc: 1,
			},
		],
	});
}

function summary(overrides: Record<string, unknown> = {}) {
	return {
		id: sessionId,
		title: "Survey the vector-store options",
		kind: "Research",
		status: "Running",
		agentDefinitionId: agentId,
		stepCount: 4,
		updatedAtUtc: 1_700_000_000_000,
		...overrides,
	};
}

describe("WorkSessionsPage", () => {
	beforeEach(() => {
		navigate.mockClear();
	});

	afterEach(() => {
		cleanup();
	});

	// Every routed page introduces itself the same way — eyebrow, icon, h2 — and this one used to hand-roll a bare
	// Title, so it was the only page in the app without the shared buildup.
	it("introduces itself with the standard page header", async () => {
		server.use(jsonRoute("get", "work-sessions", { items: [] }), agentsRoute());
		renderWithProviders(<WorkSessionsPage />);

		expect(await screen.findByText("Worker Node")).toBeDefined();
		expect(screen.getByRole("heading", { level: 2 }).textContent).toBe("Work Sessions");
	});

	it("shows skeletons while the list loads", () => {
		server.use(jsonRoute("get", "work-sessions", { items: [] }), agentsRoute());
		renderWithProviders(<WorkSessionsPage />);

		expect(screen.getByTestId("work-sessions-loading")).toBeDefined();
	});

	it("offers a create call to action when there are no sessions", async () => {
		server.use(jsonRoute("get", "work-sessions", { items: [] }), agentsRoute());
		renderWithProviders(<WorkSessionsPage />);

		await screen.findByTestId("work-sessions-empty");
		expect(screen.getByTestId("work-sessions-empty-create")).toBeDefined();
		expect(screen.queryByTestId("work-sessions-list")).toBeNull();
	});

	it("renders an inline alert with a retry when the list fails", async () => {
		server.use(
			problemDetailsRoute("get", "work-sessions", 500, { detail: "the store is unavailable" }),
			agentsRoute(),
		);
		renderWithProviders(<WorkSessionsPage />);

		const alert = await screen.findByTestId("work-sessions-error");
		expect(alert.textContent).toContain("the store is unavailable");
		expect(screen.getByTestId("work-sessions-retry")).toBeDefined();
	});

	it("renders a card per session with its status and opens the detail route on click", async () => {
		server.use(jsonRoute("get", "work-sessions", { items: [summary()] }), agentsRoute());
		renderWithProviders(<WorkSessionsPage />);

		const card = await screen.findByTestId(`work-session-card-${sessionId}`);
		expect(card.textContent).toContain("Survey the vector-store options");
		expect(screen.getByTestId(`work-session-card-status-${sessionId}`).textContent).toBe("Running");

		fireEvent.click(card);
		expect(navigate).toHaveBeenCalledWith({ to: "/work-sessions/$sessionId", params: { sessionId } });
	});

	it("creates a session from the dialog and navigates to it", async () => {
		server.use(
			jsonRoute("get", "work-sessions", { items: [] }),
			agentsRoute(),
			jsonRoute("post", "work-sessions", {
				id: sessionId,
				title: "Survey the vector-store options",
				objective: "Compare the options",
				kind: "Research",
				agentDefinitionId: agentId,
				conversationId: "cccccccc-0000-4000-8000-000000000003",
				status: "Draft",
				currentTaskId: null,
				stepCount: 0,
				maxStepsPerRun: 25,
				lastCheckpointId: null,
				createdAtUtc: 1,
				updatedAtUtc: 1,
				version: 1,
				lastSequence: 0,
			}),
		);
		renderWithProviders(<WorkSessionsPage />);

		fireEvent.click(await screen.findByTestId("work-sessions-create"));
		const dialog = await screen.findByTestId("create-work-session-dialog");
		expect(dialog).toBeDefined();

		fireEvent.change(screen.getByTestId("create-work-session-title"), {
			target: { value: "Survey the vector-store options" },
		});
		fireEvent.change(screen.getByTestId("create-work-session-objective"), {
			target: { value: "Compare the options" },
		});
		fireEvent.click(screen.getByTestId("chat-agent-selector-trigger"));
		fireEvent.click(await screen.findByTestId(`chat-agent-selector-option-${agentId}`));

		const submit = screen.getByTestId("create-work-session-submit");
		await waitFor(() => expect((submit as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(submit);

		await waitFor(() => expect(navigate).toHaveBeenCalledWith({ to: "/work-sessions/$sessionId", params: { sessionId } }));
	});
});
