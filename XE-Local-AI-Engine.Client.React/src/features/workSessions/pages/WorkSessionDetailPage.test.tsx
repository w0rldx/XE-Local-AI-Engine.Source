// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

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

describe("WorkSessionDetailPage", () => {
	beforeEach(() => {
		// jsdom's default; the desktop layout is the default under test, matching ChatDisplayShell's own reasoning.
		setViewportWidth(1024);
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
		renderWithProviders(<WorkSessionDetailPage sessionId={sessionId} />);

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
		renderWithProviders(<WorkSessionDetailPage sessionId={sessionId} />);

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
		renderWithProviders(<WorkSessionDetailPage sessionId={sessionId} />);

		const chat = await screen.findByTestId("embedded-chat");
		expect(chat.getAttribute("data-conversation")).toBe(conversationId);
		expect(chat.getAttribute("data-agent")).toBe(agentId);
		expect(chat.getAttribute("data-embedded")).toBe("true");
		expect(chat.getAttribute("data-composer-disabled")).toBe("false");
	});

	it("disables the composer once the session is terminal", async () => {
		routes(session({ status: "Completed" }));
		renderWithProviders(<WorkSessionDetailPage sessionId={sessionId} />);

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
		renderWithProviders(<WorkSessionDetailPage sessionId={sessionId} />);

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
		renderWithProviders(<WorkSessionDetailPage sessionId={sessionId} />);
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
		renderWithProviders(<WorkSessionDetailPage sessionId={sessionId} />);

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
		renderWithProviders(<WorkSessionDetailPage sessionId={sessionId} />);

		fireEvent.click(await screen.findByTestId("work-session-start"));

		await waitFor(() => expect(started).toBe(1));
	});

	it("offers a way back when the session cannot be loaded", async () => {
		server.use(problemDetailsRoute("get", `work-sessions/${sessionId}`, 404, { detail: "no such session" }));
		renderWithProviders(<WorkSessionDetailPage sessionId={sessionId} />, { withRouter: true });

		const alert = await screen.findByTestId("work-session-detail-error");
		expect(alert.textContent).toContain("no such session");
		expect(screen.getByTestId("work-session-detail-back")).toBeDefined();
	});
});
