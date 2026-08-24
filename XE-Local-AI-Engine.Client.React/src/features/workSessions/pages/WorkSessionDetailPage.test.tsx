// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ReactNode } from "react";

import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import type { ChatScope } from "@/features/chat/models/ChatModels";
import { WorkSessionDetailPage } from "@/features/workSessions/pages/WorkSessionDetailPage";
import { jsonRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The centre pane IS the real chat page; its own contract is pinned by Chat.scope.test.tsx. What this file needs is
// the SCOPE the detail page hands it and a way to fire the two overrides.
const lastScope = vi.hoisted(() => ({ current: undefined as ChatScope | undefined }));

vi.mock("@/features/chat/pages/Chat", () => ({
	Chat: ({ scope }: { scope?: ChatScope }) => {
		lastScope.current = scope;
		return (
		<div
			data-testid="embedded-chat"
			data-conversation={scope?.conversationId}
			data-agent={scope?.pinnedAgentId}
			data-composer-disabled={String(scope?.composerDisabled === true)}
			data-embedded={String(scope?.embedded === true)}
			data-resume-nonce={String(scope?.resumeNonce ?? 0)}
		>
			<button
				type="button"
				data-testid="embedded-send"
				onClick={() => {
					scope?.onSendOverride?.("check the second source").catch(() => undefined);
				}}
			>
				send
			</button>
			<button type="button" data-testid="embedded-stop" onClick={() => scope?.onStopOverride?.()}>
				stop
			</button>
		</div>
		);
	},
}));

const navigateSpy = vi.hoisted(() => vi.fn());

// The app router is built from routeTree.gen.ts; a unit test only needs the navigate CALL, not a real route match.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigateSpy,
}));

const hubMock = vi.hoisted(() => {
	const connection = { state: "Connected", on: vi.fn(), off: vi.fn(), invoke: vi.fn() };
	return {
		connection,
		acquire: vi.fn(() => ({
			connection,
			whenStarted: Promise.resolve(),
			onReconnected: vi.fn(() => vi.fn()),
			release: vi.fn(),
		})),
	};
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({ acquireHubConnection: hubMock.acquire }));

const sessionId = "aaaaaaaa-0000-4000-8000-000000000001";
const conversationId = "bbbbbbbb-0000-4000-8000-000000000002";
const agentId = "cccccccc-0000-4000-8000-000000000003";

function session(overrides: Record<string, unknown> = {}) {
	return {
		id: sessionId,
		title: "Survey the vector-store options",
		objective: "Compare the options",
		kind: "Research",
		agentDefinitionId: agentId,
		conversationId,
		status: "Running",
		currentTaskId: null,
		stepCount: 3,
		maxStepsPerRun: 25,
		lastCheckpointId: null,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		version: 1,
		lastSequence: 5,
		...overrides,
	};
}

function routes(sessionBody: Record<string, unknown> = session()) {
	server.use(
		jsonRoute("get", `work-sessions/${sessionId}`, sessionBody),
		jsonRoute("get", `work-sessions/${sessionId}/tasks`, { items: [], lastSequence: 0 }),
		jsonRoute("get", `work-sessions/${sessionId}/findings`, { items: [], lastSequence: 0 }),
		jsonRoute("get", `work-sessions/${sessionId}/artifacts`, { items: [], lastSequence: 0 }),
		jsonRoute("get", `work-sessions/${sessionId}/checkpoints`, { items: [], lastSequence: 0 }),
		jsonRoute("get", `work-sessions/${sessionId}/events`, { items: [], lastSequence: 0, hasMore: false }),
	);
}

function setViewportWidth(width: number): void {
	Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: width });
}

// The page's delete action routes through the shared confirm dialog, which throws without a provider.
const confirmResult = { value: true };
const confirmSpy = vi.fn(() => Promise.resolve(confirmResult.value));

function renderDetail(node: ReactNode, options: { withRouter?: boolean } = {}) {
	return renderWithProviders(<ConfirmContext.Provider value={{ confirm: confirmSpy }}>{node}</ConfirmContext.Provider>, options);
}

describe("WorkSessionDetailPage", () => {
	beforeEach(() => {
		// jsdom's default; the desktop layout is the default under test, matching ChatDisplayShell's own reasoning.
		setViewportWidth(1024);
		confirmResult.value = true;
		hubMock.connection.invoke.mockResolvedValue({
			sessionId,
			status: "Running",
			step: 3,
			currentTaskId: null,
			lastSeq: 5,
			events: [],
			replayTruncated: false,
		});
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the three panes side by side at desktop width", async () => {
		routes();
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		await screen.findByTestId("work-session-detail-grid");
		expect(screen.getByTestId("work-session-plan-panel")).toBeDefined();
		expect(screen.getByTestId("work-session-conversation-pane")).toBeDefined();
		expect(screen.getByTestId("work-session-side-panel")).toBeDefined();
		// No drawer toggles on desktop — the panes are already on screen.
		expect(screen.queryByTestId("work-session-plan-toggle")).toBeNull();
		expect(screen.queryByTestId("work-session-side-toggle")).toBeNull();
	});

	it("collapses to the conversation with two drawers below 1024px", async () => {
		setViewportWidth(800);
		routes();
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		await screen.findByTestId("work-session-conversation-pane");
		expect(screen.queryByTestId("work-session-detail-grid")).toBeNull();
		expect(screen.queryByTestId("work-session-plan-panel")).toBeNull();
		expect(screen.queryByTestId("work-session-side-panel")).toBeNull();

		fireEvent.click(screen.getByTestId("work-session-plan-toggle"));
		expect(await screen.findByTestId("work-session-plan-panel")).toBeDefined();

		fireEvent.click(screen.getByTestId("work-session-side-toggle"));
		expect(await screen.findByTestId("work-session-side-panel")).toBeDefined();
	});

	it("pins the owned conversation and the session's agent into the embedded chat", async () => {
		routes();
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		const chat = await screen.findByTestId("embedded-chat");
		expect(chat.getAttribute("data-conversation")).toBe(conversationId);
		expect(chat.getAttribute("data-agent")).toBe(agentId);
		expect(chat.getAttribute("data-embedded")).toBe("true");
		expect(chat.getAttribute("data-composer-disabled")).toBe("false");
	});

	it("disables the composer once the session is terminal", async () => {
		routes(session({ status: "Completed" }));
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		const chat = await screen.findByTestId("embedded-chat");
		expect(chat.getAttribute("data-composer-disabled")).toBe("true");
	});

	it("posts a follow-up through the work-session route, never a chat invocation", async () => {
		routes();
		const posted: string[] = [];
		server.use(
			http.post(localApiPath(`work-sessions/${sessionId}/messages`), async ({ request }) => {
				const body = (await request.json()) as { text: string };
				posted.push(body.text);
				return HttpResponse.json({ messageId: "dddddddd-0000-4000-8000-000000000004", conversationId }, { status: 202 });
			}),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("embedded-send"));

		await waitFor(() => expect(posted).toEqual(["check the second source"]));
	});

	it("surfaces a rejected follow-up inline AND rejects the override, which is what keeps the draft", async () => {
		routes();
		server.use(
			problemDetailsRoute("post", `work-sessions/${sessionId}/messages`, 400, {
				detail: "Message exceeds the node's size limit.",
			}),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);
		await screen.findByTestId("embedded-chat");

		// Driven directly rather than through the button, because the REJECTION is the contract: ChatInputArea only
		// keeps the draft when the returned promise rejects (see Chat.scope.test.tsx).
		await expect(lastScope.current?.onSendOverride?.("an oversized follow-up")).rejects.toThrow();

		const inlineError = await screen.findByTestId("work-session-follow-up-error");
		expect(inlineError.textContent).toContain("size limit");
	});

	it("maps the chat stop button onto pause", async () => {
		routes();
		let paused = 0;
		server.use(
			http.post(localApiPath(`work-sessions/${sessionId}/pause`), () => {
				paused += 1;
				return HttpResponse.json(session({ status: "Paused" }));
			}),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("embedded-stop"));

		await waitFor(() => expect(paused).toBe(1));
	});

	it("sends the lifecycle command the status offers", async () => {
		routes(session({ status: "Draft" }));
		let started = 0;
		server.use(
			http.post(localApiPath(`work-sessions/${sessionId}/start`), () => {
				started += 1;
				return HttpResponse.json(session({ status: "Running" }), { status: 202 });
			}),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("work-session-start"));

		await waitFor(() => expect(started).toBe(1));
	});

	it("saves a title and objective edit, carrying the unchanged agent through the PATCH", async () => {
		routes(session({ status: "Paused" }));
		const patched: Array<{ title: string; objective: string; agentDefinitionId: string }> = [];
		server.use(
			http.patch(localApiPath(`work-sessions/${sessionId}`), async ({ request }) => {
				patched.push((await request.json()) as { title: string; objective: string; agentDefinitionId: string });
				return HttpResponse.json(session({ status: "Paused", title: "Renamed" }));
			}),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("work-session-actions"));
		fireEvent.click(await screen.findByTestId("work-session-edit"));
		fireEvent.change(await screen.findByTestId("edit-work-session-title"), { target: { value: "Renamed" } });
		fireEvent.click(screen.getByTestId("edit-work-session-submit"));

		await waitFor(() => expect(patched).toHaveLength(1));
		expect(patched[0]).toEqual({ title: "Renamed", objective: "Compare the options", agentDefinitionId: agentId });
	});

	it("locks the objective while a step is live", async () => {
		routes(session({ status: "Running" }));
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("work-session-actions"));
		fireEvent.click(await screen.findByTestId("work-session-edit"));

		expect(((await screen.findByTestId("edit-work-session-objective")) as HTMLTextAreaElement).disabled).toBe(true);
		expect((screen.getByTestId("edit-work-session-title") as HTMLInputElement).disabled).toBe(false);
	});

	it("deletes only after the operator confirms, then leaves the route", async () => {
		routes(session({ status: "Paused" }));
		let deleted = 0;
		server.use(
			http.delete(localApiPath(`work-sessions/${sessionId}`), () => {
				deleted += 1;
				return new HttpResponse(null, { status: 204 });
			}),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		confirmResult.value = false;
		fireEvent.click(await screen.findByTestId("work-session-actions"));
		fireEvent.click(await screen.findByTestId("work-session-delete"));
		await waitFor(() => expect(confirmSpy).toHaveBeenCalled());
		expect(deleted).toBe(0);
		expect(navigateSpy).not.toHaveBeenCalled();

		confirmResult.value = true;
		fireEvent.click(screen.getByTestId("work-session-actions"));
		fireEvent.click(await screen.findByTestId("work-session-delete"));

		await waitFor(() => expect(deleted).toBe(1));
		// The delete also removes the owned conversation, so the route must not stay open on it.
		await waitFor(() => expect(navigateSpy).toHaveBeenCalledWith({ to: "/work-sessions" }));
	});

	it("keeps the operator on the page and explains a refused delete", async () => {
		routes(session({ status: "Running" }));
		server.use(
			problemDetailsRoute("delete", `work-sessions/${sessionId}`, 409, { detail: "Cancel the session before deleting it." }),
		);
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("work-session-actions"));
		fireEvent.click(await screen.findByTestId("work-session-delete"));

		const alert = await screen.findByTestId("work-session-delete-error");
		expect(alert.textContent).toContain("Cancel the session before deleting it.");
		expect(navigateSpy).not.toHaveBeenCalled();
	});

	it("offers a way back when the session cannot be loaded", async () => {
		server.use(problemDetailsRoute("get", `work-sessions/${sessionId}`, 404, { detail: "no such session" }));
		renderDetail(<WorkSessionDetailPage sessionId={sessionId} />, { withRouter: true });

		const alert = await screen.findByTestId("work-session-detail-error");
		expect(alert.textContent).toContain("no such session");
		expect(screen.getByTestId("work-session-detail-back")).toBeDefined();
	});
});
