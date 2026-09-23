// @vitest-environment jsdom

// Chat workflow mode through the real Chat page: the picker, the send routing (the exact body the messages endpoint
// receives, and that normal mode still streams), the three 409 refusals, a reload restoring the mode from the bound
// runs, the status card, the input banner and the activity block. The chat transport is the page's own mocked adapter
// (as in the other Chat page tests); every graph-workflows route goes over MSW so the generated client and its
// response validation are real.

import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	Link: ({ children, to, ...props }: { children: ReactNode; to: string; search?: unknown }) => {
		const { search: _search, ...rest } = props;
		return (
			<a href={to} {...rest}>
				{children}
			</a>
		);
	},
}));

vi.mock("@/features/chat/api/NodeChatAdapter", () => ({
	nodeChatAdapter: {
		listConversations: vi.fn(),
		getConversation: vi.fn(),
		sendMessage: vi.fn(),
		regenerateMessage: vi.fn(),
		resumeConversation: vi.fn(() => ({
			[Symbol.asyncIterator]: () => ({ next: async () => ({ done: true, value: undefined }) }),
		})),
		deleteConversation: vi.fn(),
		renameConversation: vi.fn(),
		setConversationPinned: vi.fn(),
		setConversationArchived: vi.fn(),
		branchConversation: vi.fn(),
		listMessageRevisions: vi.fn(),
		setMessageFeedback: vi.fn(),
		createConversation: vi.fn(),
		cancelMessage: vi.fn(),
		persistSelectedPath: vi.fn(),
	},
}));

vi.mock("@/features/chat/api/NodeChatConnection", () => ({
	nodeChatConnection: {
		status: "connected",
		subscribe: vi.fn(() => () => undefined),
		ensureConnection: vi.fn(() => Promise.resolve(undefined)),
	},
}));

// The run hub is `useGraphWorkflowRunHub.test.tsx`'s subject; here a live run must simply not open a socket. The
// watermark is the one thing a test moves, to stand in for an admitted ping.
const hubWatermark = vi.hoisted(() => {
	let value = 0;
	const listeners = new Set<() => void>();
	return {
		get: () => value,
		subscribe: (listener: () => void) => {
			listeners.add(listener);
			return () => listeners.delete(listener);
		},
		set: (next: number) => {
			value = next;
			for (const listener of listeners) {
				listener();
			}
		},
	};
});
vi.mock("@/features/graphWorkflows/hooks/useGraphWorkflowRunHub", async () => {
	const { useSyncExternalStore } = await import("react");
	return {
		useGraphWorkflowRunHub: () => ({
			connectionState: "connected",
			watermark: useSyncExternalStore(hubWatermark.subscribe, hubWatermark.get),
		}),
	};
});

import i18next from "i18next";

import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { nodeChatStreamEventTypes } from "@/features/chat/api/NodeChatStreamState";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";
import { Chat } from "@/features/chat/pages/Chat";
import { useChatWorkflowStore } from "@/features/chat/workflow/ChatWorkflowStore";
import type {
	GraphWorkflowConversationRunResponse,
	GraphWorkflowNodeRunSummaryResponse,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import {
	agentNodeRunDetail,
	chatGraph,
	chatWorkflowEvents,
	graphWorkflowConversationRun,
	graphWorkflowDefinition,
	graphWorkflowDefinitionSummary,
	graphWorkflowRun,
	graphWorkflowRunSummary,
	graphWorkflowTestGuid,
	graphWorkflowTestIds,
	makeNodeRun,
} from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const conversationId = "11111111-1111-4111-8111-111111111111";
const definitionId = graphWorkflowTestIds.definition;
const standardDefinitionId = graphWorkflowTestGuid(80);
const otherChatDefinitionId = graphWorkflowTestGuid(81);
const deletedDefinitionId = graphWorkflowTestGuid(82);
const runId = graphWorkflowTestIds.run;
const triggerMessageId = graphWorkflowTestGuid(90);

// Ambient routes the page reads before anything workflow-related: the chat model list, the composer's commands,
// agents and knowledge base, and the conversation's attachment list.
setupMswServer(
	jsonRoute("get", "models", { isAvailable: true, items: [] }),
	jsonRoute("get", "automation/commands", { items: [] }),
	jsonRoute("get", "agents", { items: [] }),
	jsonRoute("get", "knowledge-base/documents", { items: [], embeddingModel: "nomic-embed-text", embeddingModelAvailable: true }),
	jsonRoute("get", `chat/conversations/${conversationId}/uploads`, { items: [] }),
	jsonRoute("get", "graph-workflows/definitions", {
		definitions: [
			graphWorkflowDefinitionSummary({ id: definitionId, name: "Support triage", kind: "Chat" }),
			graphWorkflowDefinitionSummary({ id: standardDefinitionId, name: "Nightly triage", kind: "Standard" }),
			graphWorkflowDefinitionSummary({ id: otherChatDefinitionId, name: "Code review", kind: "Chat" }),
		],
	}),
	jsonRoute(
		"get",
		`graph-workflows/definitions/${definitionId}`,
		graphWorkflowDefinition({ id: definitionId, name: "Support triage", kind: "Chat", graph: chatGraph }),
	),
);

const adapter = vi.mocked(nodeChatAdapter);

function conversation(overrides: Partial<ChatConversationModel> = {}): ChatConversationModel {
	return {
		id: conversationId,
		title: "Thread",
		origin: "local",
		createdAt: "2026-09-23T00:00:00.000Z",
		updatedAt: "2026-09-23T00:00:00.000Z",
		messages: [],
		...overrides,
	};
}

const userTurn = {
	id: triggerMessageId,
	conversationId,
	role: "user" as const,
	content: "Build me a CLI",
	status: "completed" as const,
	createdAt: "2026-09-23T00:00:01.000Z",
	sortOrder: 1,
};

function runsRoute(runs: readonly GraphWorkflowConversationRunResponse[]) {
	return jsonRoute("get", `graph-workflows/conversations/${conversationId}/runs`, { runs });
}

function runRoutes(status: string, nodeRuns: readonly GraphWorkflowNodeRunSummaryResponse[]) {
	return [
		jsonRoute(
			"get",
			`graph-workflows/runs/${runId}`,
			graphWorkflowRun({ run: graphWorkflowRunSummary({ status }), graph: chatGraph, nodeRuns: [...nodeRuns] }),
		),
		jsonRoute("get", `graph-workflows/runs/${runId}/events`, chatWorkflowEvents()),
		http.get(localApiPath(`graph-workflows/runs/${runId}/nodes/:nodeKey`), ({ params }) =>
			HttpResponse.json(
				agentNodeRunDetail({
					nodeKey: String(params["nodeKey"]),
					kind: "LlmCall",
					output: { status: "succeeded", attempt: 1, branch: null, output: { text: "fn main() {}" } },
				}),
			),
		),
	];
}

/** Records every body the messages endpoint receives and answers each with the next queued response. */
function messagesRoute(...responses: readonly ((body: unknown) => Response)[]) {
	const bodies: unknown[] = [];
	server.use(
		http.post(localApiPath(`graph-workflows/conversations/${conversationId}/messages`), async ({ request }) => {
			const body: unknown = await request.json();
			bodies.push(body);
			const respond =
				responses[bodies.length - 1] ??
				(() => HttpResponse.json({ runId, messageId: graphWorkflowTestGuid(92), action: "started" }, { status: 202 }));
			return respond(body);
		}),
	);
	return bodies;
}

function conflict(conflictType: string) {
	return () =>
		HttpResponse.json(
			{ type: "about:blank", title: "Conflict", status: 409, detail: "Refused.", conflictType, traceId: "t" },
			{ status: 409, headers: { "content-type": "application/problem+json" } },
		);
}

function renderChat(confirm = vi.fn().mockResolvedValue(true)) {
	renderWithProviders(
		<ConfirmContext.Provider value={{ confirm }}>
			<Chat />
		</ConfirmContext.Provider>,
	);
	return { confirm };
}

async function pickWorkflow(): Promise<void> {
	fireEvent.click(await screen.findByTestId("chat-workflow-selector-trigger"));
	fireEvent.click(await screen.findByTestId(`chat-workflow-selector-option-${definitionId}`));
	await waitFor(() => expect(screen.getByTestId("chat-workflow-selector-trigger").textContent).toBe("Support triage"));
}

async function typeAndSend(text: string): Promise<HTMLTextAreaElement> {
	// The conversation must be selected first, or the send would create a new one.
	await screen.findByTestId(`conversation-item-${conversationId}`);
	const input = (await screen.findByTestId("chat-input")) as HTMLTextAreaElement;
	fireEvent.change(input, { target: { value: text } });
	fireEvent.click(screen.getByTestId("chat-send-button"));
	return input;
}

const initialStore = useChatWorkflowStore.getState();

describe("Chat workflow mode", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		localStorage.clear();
		useChatWorkflowStore.setState(initialStore, true);
		hubWatermark.set(0);
		adapter.listConversations.mockResolvedValue({ conversations: [conversation()] });
		adapter.getConversation.mockResolvedValue(conversation());
	});

	afterEach(() => {
		cleanup();
		localStorage.clear();
	});

	it("offers only Chat workflows in the picker", async () => {
		server.use(runsRoute([]));
		renderChat();

		fireEvent.click(await screen.findByTestId("chat-workflow-selector-trigger"));

		const dropdown = await screen.findByTestId("chat-workflow-selector-dropdown");
		expect(within(dropdown).getByTestId(`chat-workflow-selector-option-${definitionId}`).textContent).toContain("Support triage");
		expect(within(dropdown).queryByTestId(`chat-workflow-selector-option-${standardDefinitionId}`)).toBeNull();
	});

	it("posts a workflow-mode send to the messages endpoint with the exact body and never opens the chat stream", async () => {
		server.use(runsRoute([]));
		const bodies = messagesRoute();
		renderChat();
		await pickWorkflow();

		await typeAndSend("Build me a CLI");

		await waitFor(() => expect(bodies).toHaveLength(1));
		expect(bodies[0]).toEqual({
			requestId: expect.stringMatching(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/),
			definitionId,
			content: "Build me a CLI",
			attachmentFileIds: null,
		});
		expect(adapter.sendMessage).not.toHaveBeenCalled();
		// Accepted, so the draft clears exactly as a normal send's does.
		await waitFor(() => expect((screen.getByTestId("chat-input") as HTMLTextAreaElement).value).toBe(""));
	});

	it("keeps normal mode on the chat stream and never posts to the workflow endpoint", async () => {
		server.use(runsRoute([]));
		const bodies = messagesRoute();
		adapter.sendMessage.mockImplementation(() => ({
			async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
				yield {
					type: nodeChatStreamEventTypes.assistantCompleted,
					conversationId,
					messageId: "assistant-1",
					requestId: "request-1",
					status: "completed",
					sequence: 1,
					occurredAtUtc: 1_700_000_000_000,
					content: "hi",
				};
			},
		}));
		renderChat();
		expect((await screen.findByTestId("chat-workflow-selector-trigger")).textContent).toBe("No workflow");

		await typeAndSend("hello");

		await waitFor(() => expect(adapter.sendMessage).toHaveBeenCalledTimes(1));
		expect(bodies).toHaveLength(0);
	});

	it("asks before starting a finished workflow again, then resends with confirmRerun", async () => {
		server.use(runsRoute([]));
		const bodies = messagesRoute(conflict("GraphWorkflowRerunConfirmationRequired"));
		const { confirm } = renderChat();
		await pickWorkflow();

		await typeAndSend("Again please");

		await waitFor(() => expect(bodies).toHaveLength(2));
		expect(confirm).toHaveBeenCalledWith(
			expect.objectContaining({
				description: "This workflow has completed. Sending this message will start 'Support triage' again.",
			}),
		);
		expect(bodies[1]).toEqual({ ...(bodies[0] as object), confirmRerun: true });
	});

	it("keeps the draft and sends nothing more when the rerun is declined", async () => {
		server.use(runsRoute([]));
		const bodies = messagesRoute(conflict("GraphWorkflowRerunConfirmationRequired"));
		const { confirm } = renderChat(vi.fn().mockResolvedValue(false));
		await pickWorkflow();

		const input = await typeAndSend("Again please");

		await waitFor(() => expect(confirm).toHaveBeenCalledTimes(1));
		expect(bodies).toHaveLength(1);
		expect(input.value).toBe("Again please");
	});

	it("says the workflow is busy inline and keeps the draft", async () => {
		server.use(runsRoute([]));
		messagesRoute(conflict("GraphWorkflowRunBusy"));
		renderChat();
		await pickWorkflow();
		const input = await typeAndSend("Are you done?");

		expect((await screen.findByTestId("chat-workflow-notice")).textContent).toContain(
			"The workflow is still running. Stop it or wait for it to finish.",
		);
		expect(input.value).toBe("Are you done?");
	});

	it("says the selection changed when an answer names another workflow than the parked run's", async () => {
		server.use(runsRoute([]));
		messagesRoute(conflict("GraphWorkflowRunConflict"));
		renderChat();
		await pickWorkflow();

		const input = await typeAndSend("My answer");

		expect((await screen.findByTestId("chat-workflow-notice")).textContent).toContain(
			"A different workflow is waiting for your answer. Select it again to answer, or Stop it first.",
		);
		expect(input.value).toBe("My answer");
	});

	it("says the workflow refuses attachments inline", async () => {
		server.use(runsRoute([]));
		messagesRoute(conflict("GraphWorkflowAttachmentsNotAccepted"));
		renderChat();
		await pickWorkflow();

		await typeAndSend("See the file");

		expect((await screen.findByTestId("chat-workflow-notice")).textContent).toContain(
			"This workflow does not accept attachments. Remove them and send again.",
		);
	});

	it("restores workflow mode after a reload from the bound runs, with the status card and the activity block", async () => {
		adapter.getConversation.mockResolvedValue(conversation({ messages: [userTurn] }));
		adapter.listConversations.mockResolvedValue({ conversations: [conversation({ messages: [userTurn] })] });
		server.use(
			runsRoute([graphWorkflowConversationRun({ run: graphWorkflowRunSummary({ status: "Completed" }) })]),
			...runRoutes("Completed", [
				makeNodeRun({ id: graphWorkflowTestGuid(1), nodeKey: "start", kind: "Start", status: "Succeeded" }),
				makeNodeRun({ id: graphWorkflowTestGuid(2), nodeKey: "code", kind: "LlmCall", status: "Succeeded" }),
			]),
		);
		renderChat();

		await waitFor(() => expect(screen.getByTestId("chat-workflow-selector-trigger").textContent).toBe("Support triage"));
		expect(screen.getByTestId("chat-input").getAttribute("placeholder")).toBe("Message Support triage");
		expect((await screen.findByTestId("chat-workflow-status-terminal")).textContent).toBe("Completed");
		const block = await screen.findByTestId(`chat-workflow-activity-${runId}`);
		expect(await within(block).findByTestId("chat-workflow-activity-published-code")).toBeTruthy();

		// Hiding the activity leaves the messages and the status card, and the choice survives a reload.
		fireEvent.click(screen.getByTestId("chat-workflow-activity-toggle"));
		await waitFor(() => expect(screen.queryByTestId(`chat-workflow-activity-${runId}`)).toBeNull());
		expect(screen.getByTestId("chat-workflow-status-card")).toBeTruthy();
		expect(localStorage.getItem("xe-node-chat-workflow-show-activity")).toBe("false");
	});

	it("asks for input through a banner and answers with the next send", async () => {
		server.use(
			runsRoute([
				graphWorkflowConversationRun({
					run: graphWorkflowRunSummary({ status: "WaitingForApproval" }),
					pendingInput: { nodeKey: "ask", prompt: "What should I build?" },
				}),
			]),
			...runRoutes("WaitingForApproval", [
				makeNodeRun({ nodeKey: "ask", kind: "ChatInput", status: "WaitingForApproval", pendingDecisionKind: "Answer" }),
			]),
		);
		const bodies = messagesRoute(() =>
			HttpResponse.json({ runId, messageId: graphWorkflowTestGuid(93), action: "answered" }, { status: 202 }),
		);
		renderChat();

		// The node label comes off the run's pinned graph, so it replaces the bare key once the run detail lands.
		await waitFor(() =>
			expect(screen.getByTestId("chat-workflow-input-banner").textContent).toBe(
				"Workflow needs your input — Ask: What should I build?",
			),
		);
		expect(screen.getByTestId("chat-input").getAttribute("placeholder")).toBe("Answer the workflow's question");
		expect(screen.queryByTestId("chat-workflow-locked-hint")).toBeNull();

		await typeAndSend("A CLI");

		await waitFor(() => expect(bodies).toHaveLength(1));
		expect(bodies[0]).toMatchObject({ definitionId, content: "A CLI", attachmentFileIds: null });
	});

	it("sends once when Send is pressed twice before the first is accepted, into one new conversation", async () => {
		const created = conversation({ title: "Build me a CLI" });
		adapter.listConversations.mockResolvedValue({ conversations: [] });
		adapter.createConversation.mockResolvedValue(created);
		adapter.getConversation.mockResolvedValue(created);
		let release: (() => void) | undefined;
		const bodies: unknown[] = [];
		server.use(
			runsRoute([]),
			http.post(localApiPath(`graph-workflows/conversations/${conversationId}/messages`), async ({ request }) => {
				bodies.push(await request.json());
				await new Promise<void>((resolve) => {
					release = resolve;
				});
				return HttpResponse.json({ runId, messageId: graphWorkflowTestGuid(92), action: "started" }, { status: 202 });
			}),
		);
		renderChat();
		// Picked before any conversation exists: the first send carries the pick onto the conversation it creates.
		await pickWorkflow();

		const input = (await screen.findByTestId("chat-input")) as HTMLTextAreaElement;
		fireEvent.change(input, { target: { value: "Build me a CLI" } });
		fireEvent.click(screen.getByTestId("chat-send-button"));
		fireEvent.keyDown(input, { key: "Enter" });
		fireEvent.click(screen.getByTestId("chat-send-button"));

		await waitFor(() => expect(bodies).toHaveLength(1));
		expect(input.disabled).toBe(false);
		release?.();
		await waitFor(() => expect(input.value).toBe(""));
		expect(bodies).toHaveLength(1);
		expect(adapter.createConversation).toHaveBeenCalledTimes(1);
		expect(useChatWorkflowStore.getState().selectedDefinitionByConversation).toEqual({ [conversationId]: definitionId });
	});

	it("answers a parked input with the PARKED run's workflow and locks the picker while the run is live", async () => {
		useChatWorkflowStore.getState().actions.selectDefinition(conversationId, otherChatDefinitionId);
		server.use(
			jsonRoute(
				"get",
				`graph-workflows/definitions/${otherChatDefinitionId}`,
				graphWorkflowDefinition({ id: otherChatDefinitionId, name: "Code review", kind: "Chat", graph: chatGraph }),
			),
			runsRoute([
				graphWorkflowConversationRun({
					run: graphWorkflowRunSummary({ status: "WaitingForApproval" }),
					pendingInput: { nodeKey: "ask", prompt: "What should I build?" },
				}),
			]),
			...runRoutes("WaitingForApproval", [
				makeNodeRun({ nodeKey: "ask", kind: "ChatInput", status: "WaitingForApproval", pendingDecisionKind: "Answer" }),
			]),
		);
		const bodies = messagesRoute(() =>
			HttpResponse.json({ runId, messageId: graphWorkflowTestGuid(93), action: "answered" }, { status: 202 }),
		);
		renderChat();

		await screen.findByTestId("chat-workflow-input-banner");
		expect(screen.getByTestId("chat-workflow-selector-trigger")).toHaveProperty("disabled", true);
		await typeAndSend("A CLI");

		await waitFor(() => expect(bodies).toHaveLength(1));
		expect(bodies[0]).toMatchObject({ definitionId, content: "A CLI" });
	});

	it("leaves a run of a deleted workflow in normal chat mode but still shows its status card", async () => {
		server.use(
			runsRoute([
				graphWorkflowConversationRun({
					definitionId: deletedDefinitionId,
					definitionName: null,
					run: graphWorkflowRunSummary({ status: "Completed", definitionId: deletedDefinitionId }),
				}),
			]),
			...runRoutes("Completed", [makeNodeRun({ nodeKey: "code", kind: "LlmCall", status: "Succeeded" })]),
		);
		renderChat();

		expect((await screen.findByTestId("chat-workflow-status-name")).textContent).toBe("Deleted workflow");
		await waitFor(() => expect(screen.getByTestId("chat-input").getAttribute("placeholder")).toBe("Type your message"));
		expect(screen.getByTestId("chat-workflow-selector-trigger").textContent).toBe("No workflow");
	});

	it("remembers an explicit 'No workflow' across a reload of a conversation with finished runs", async () => {
		server.use(
			runsRoute([graphWorkflowConversationRun({ run: graphWorkflowRunSummary({ status: "Completed" }) })]),
			...runRoutes("Completed", [makeNodeRun({ nodeKey: "code", kind: "LlmCall", status: "Succeeded" })]),
		);
		const { unmount } = renderWithProviders(
			<ConfirmContext.Provider value={{ confirm: vi.fn() }}>
				<Chat />
			</ConfirmContext.Provider>,
		);
		await waitFor(() => expect(screen.getByTestId("chat-workflow-selector-trigger").textContent).toBe("Support triage"));
		fireEvent.click(screen.getByTestId("chat-workflow-selector-trigger"));
		fireEvent.click(await screen.findByTestId("chat-workflow-selector-option-none"));
		unmount();

		vi.resetModules();
		const { useChatWorkflowStore: reloadedStore } = await import("@/features/chat/workflow/ChatWorkflowStore");
		expect(reloadedStore.getState().selectedDefinitionByConversation).toEqual({ [conversationId]: "" });
	});

	it("offers Dismiss as soon as the run detail says the cancel landed, even while the run list still says Cancelling", async () => {
		server.use(
			runsRoute([graphWorkflowConversationRun({ run: graphWorkflowRunSummary({ status: "Cancelling" }) })]),
			...runRoutes("Cancelled", [makeNodeRun({ nodeKey: "code", kind: "LlmCall", status: "Cancelled" })]),
		);
		renderChat();

		expect((await screen.findByTestId("chat-workflow-status-terminal")).textContent).toBe("Cancelled during Code");
		expect(screen.getByTestId("chat-workflow-status-dismiss")).toBeTruthy();
		expect(screen.queryByTestId("chat-workflow-status-stop")).toBeNull();
	});

	it("locks the composer while the run works and Stops it through the cancel endpoint", async () => {
		const cancelled: string[] = [];
		server.use(
			runsRoute([graphWorkflowConversationRun({ run: graphWorkflowRunSummary({ status: "Running" }) })]),
			...runRoutes("Running", [makeNodeRun({ nodeKey: "code", kind: "LlmCall", status: "Running", completedAtUtc: null })]),
			http.post(localApiPath(`graph-workflows/runs/${runId}/cancel`), () => {
				cancelled.push(runId);
				return HttpResponse.json({ runId, status: "Cancelling" }, { status: 202 });
			}),
		);
		renderChat();

		expect((await screen.findByTestId("chat-workflow-locked-hint")).textContent).toBe("Workflow running — Stop it or wait.");
		await waitFor(() => expect(screen.getByTestId("chat-input")).toHaveProperty("disabled", true));

		fireEvent.click(await screen.findByTestId("chat-workflow-status-stop"));

		await waitFor(() => expect(cancelled).toEqual([runId]));
	});

	it("re-reads the conversation on every hub ping, so a mid-run publish shows up whatever the trail's length", async () => {
		server.use(
			runsRoute([graphWorkflowConversationRun({ run: graphWorkflowRunSummary({ status: "Running" }) })]),
			...runRoutes("Running", [makeNodeRun({ nodeKey: "code", kind: "LlmCall", status: "Running", completedAtUtc: null })]),
		);
		renderChat();
		await screen.findByTestId("chat-workflow-locked-hint");
		await waitFor(() => expect(adapter.getConversation).toHaveBeenCalled());
		const reads = adapter.getConversation.mock.calls.length;

		act(() => hubWatermark.set(7));

		await waitFor(() => expect(adapter.getConversation.mock.calls.length).toBeGreaterThan(reads));
	});

	/** Holds every bound-runs read until the returned `release` answers it with `respond`. */
	function heldRunsRoute() {
		let release: (respond: () => Response) => void = () => undefined;
		const held = new Promise<() => Response>((resolve) => {
			release = resolve;
		});
		server.use(http.get(localApiPath(`graph-workflows/conversations/${conversationId}/runs`), async () => (await held)()));
		return (respond: () => Response) => release(respond);
	}

	it("defers a send made before the bound runs load, then answers the parked run instead of the model", async () => {
		const release = heldRunsRoute();
		server.use(
			...runRoutes("WaitingForApproval", [
				makeNodeRun({ nodeKey: "ask", kind: "ChatInput", status: "WaitingForApproval", pendingDecisionKind: "Answer" }),
			]),
		);
		const bodies = messagesRoute(() =>
			HttpResponse.json({ runId, messageId: graphWorkflowTestGuid(93), action: "answered" }, { status: 202 }),
		);
		renderChat();

		// The composer stays usable, but the send neither streams nor posts until the binding is known.
		const input = await typeAndSend("A CLI");
		expect(input.disabled).toBe(false);
		expect(adapter.sendMessage).not.toHaveBeenCalled();
		expect(bodies).toHaveLength(0);

		release(() =>
			HttpResponse.json({
				runs: [
					graphWorkflowConversationRun({
						run: graphWorkflowRunSummary({ status: "WaitingForApproval" }),
						pendingInput: { nodeKey: "ask", prompt: "What should I build?" },
					}),
				],
			}),
		);

		await waitFor(() => expect(bodies).toHaveLength(1));
		expect(bodies[0]).toMatchObject({ definitionId, content: "A CLI", attachmentFileIds: null });
		expect(adapter.sendMessage).not.toHaveBeenCalled();
		await waitFor(() => expect(input.value).toBe(""));
	});

	it("keeps the draft and offers Retry when the bound runs cannot be read for a deferred send", async () => {
		const release = heldRunsRoute();
		const bodies = messagesRoute();
		renderChat();

		const input = await typeAndSend("hello");
		release(() => HttpResponse.json({ title: "Boom", status: 500 }, { status: 500 }));

		const notice = await screen.findByTestId("chat-workflow-runs-error");
		expect(notice.textContent).toContain(i18next.t("pages.chat.workflow.notice.runsFailed"));
		expect(within(notice).getByRole("button", { name: i18next.t("common.retry") })).toBeTruthy();
		expect(input.value).toBe("hello");
		expect(input.disabled).toBe(false);
		expect(adapter.sendMessage).not.toHaveBeenCalled();
		expect(bodies).toHaveLength(0);
	});
});
