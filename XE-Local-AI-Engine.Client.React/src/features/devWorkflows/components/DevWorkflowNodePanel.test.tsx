// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import type { ChatScope } from "@/features/chat/models/ChatModels";
import { DevWorkflowNodePanel } from "@/features/devWorkflows/components/DevWorkflowNodePanel";
import type {
	DevWorkflowNodeRunDetailResponse,
	DevWorkflowRunEventResponse,
	DevWorkflowRunResponse,
} from "@/features/devWorkflows/models/DevWorkflowModels";
import {
	devWorkflowNodeRunDetail,
	devWorkflowNodeRunSummary,
	devWorkflowRun,
	devWorkflowRunEvent,
} from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

const navigate = vi.hoisted(() => vi.fn());

vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigate,
}));

// The transcript IS the real chat page, whose own contract is pinned by Chat.scope.test.tsx. What this file owes is
// the SCOPE the node panel hands it — and, for a purged session, that it is never handed one at all.
const lastScope = vi.hoisted(() => ({ current: undefined as ChatScope | undefined }));

vi.mock("@/features/chat/pages/Chat", () => ({
	Chat: ({ scope }: { scope?: ChatScope }) => {
		lastScope.current = scope;
		return <div data-testid="embedded-chat" data-conversation={scope?.conversationId} />;
	},
}));

// The agent panel subscribes to the work-session hub for its resume nonce (R-C6). A unit test needs the subscription
// not to reach the network, not a real one.
const hubMock = vi.hoisted(() => {
	// `invoke` answers a promise because the hook's own cleanup unsubscribes through it and chains off the result.
	const connection = { state: "Connected", on: vi.fn(), off: vi.fn(), invoke: vi.fn(() => Promise.resolve()) };
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

const sessionId = "88888888-8888-4888-8888-888888888888";
const conversationId = "77777777-7777-4777-8777-777777777777";
const agentDefinitionId = "66666666-6666-4666-8666-666666666666";

interface PanelOptions {
	readonly onShowArtifacts?: () => void;
	/** The run's loaded event pages, exactly as the detail page hands them over — unfiltered, every node's rows. */
	readonly events?: readonly DevWorkflowRunEventResponse[];
	readonly run?: DevWorkflowRunResponse;
}

function renderPanel(nodeRun: DevWorkflowNodeRunDetailResponse, options: PanelOptions = {}) {
	const onShowArtifacts = options.onShowArtifacts ?? vi.fn();
	renderWithProviders(
		<ConfirmProvider>
			<DevWorkflowNodePanel
				nodeRun={nodeRun}
				isPending={false}
				isDeciding={false}
				events={options.events}
				run={options.run}
				onDecide={vi.fn()}
				onShowArtifacts={onShowArtifacts}
				onClose={vi.fn()}
			/>
		</ConfirmProvider>,
	);
	return { onShowArtifacts };
}

/** `node.interrupted` rows for the fixture node, which is what the panel counts restarts from. */
function interruptedEvents(count: number): readonly DevWorkflowRunEventResponse[] {
	return Array.from({ length: count }, (_, index) =>
		devWorkflowRunEvent({
			id: `interrupted-${index}`,
			sequence: index + 1,
			eventType: "node.interrupted",
			nodeRunId: devWorkflowNodeRunDetail().id,
		}),
	);
}

describe("DevWorkflowNodePanel", () => {
	beforeEach(() => {
		navigate.mockClear();
		hubMock.acquire.mockClear();
		lastScope.current = undefined;
	});

	afterEach(() => {
		cleanup();
	});

	it("keeps the link-out to the full session view beside the embedded transcript, not instead of it", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Agent", workSessionId: sessionId, workSessionAvailable: true }));

		fireEvent.click(screen.getByTestId("dev-workflow-node-session-link"));

		expect(navigate).toHaveBeenCalledWith({ to: "/work-sessions/$sessionId", params: { sessionId } });
	});

	it("embeds the node's transcript with the composer taken away, because the runtime is the single writer", () => {
		renderPanel(
			devWorkflowNodeRunDetail({
				nodeType: "Agent",
				workSessionId: sessionId,
				conversationId,
				agentDefinitionId,
				workSessionAvailable: true,
			}),
		);

		expect(screen.getByTestId("dev-workflow-node-transcript")).toBeDefined();
		// N2: a follow-up typed here would be a second writer of invocations on the node's conversation.
		expect(lastScope.current).toMatchObject({
			conversationId,
			pinnedAgentId: agentDefinitionId,
			composerDisabled: true,
			embedded: true,
		});
		expect(typeof lastScope.current?.resumeNonce).toBe("number");
		// The runtime writes the invocations, so the panel offers neither override — a redirected composer would be
		// the second writer this scope exists to prevent.
		expect(lastScope.current?.onSendOverride).toBeUndefined();
	});

	it("says the transcript is gone — not that the node is empty — when the work session was purged", () => {
		renderPanel(
			devWorkflowNodeRunDetail({
				nodeType: "Agent",
				workSessionId: sessionId,
				conversationId,
				workSessionAvailable: false,
			}),
		);

		// The node-run row outlives its session on purpose (the reference is loose), so the UI must name WHICH thing is
		// missing: the workflow-owned events and artifacts are still there.
		expect(screen.getByTestId("dev-workflow-node-session-purged")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-node-session-link")).toBeNull();
		// And nothing mounts against the dead conversation id: an empty chat pane is indistinguishable from a session
		// that has simply not spoken yet, which was the explicitly rejected round-1 behaviour.
		expect(screen.queryByTestId("embedded-chat")).toBeNull();
		expect(hubMock.acquire).not.toHaveBeenCalled();
	});

	it("reports restart survival from the event log, which is the module's whole claim", () => {
		renderPanel(devWorkflowNodeRunDetail({ sessionResumes: 0 }), { events: interruptedEvents(1) });

		expect(screen.getByTestId("dev-workflow-node-interrupted").textContent).toBe("interrupted and re-dispatched 1×");
	});

	it("does not pass step-budget parking off as a restart — they are different facts, shown separately", () => {
		// Live proof of the bug this replaces: a node that had NEVER been interrupted parked 4× and the pane claimed
		// "resumed 4×", while the node that actually survived a restart showed nothing at all.
		renderPanel(devWorkflowNodeRunDetail({ sessionResumes: 4 }), { events: interruptedEvents(0) });

		expect(screen.getByTestId("dev-workflow-node-resumes").textContent).toBe("paused for step budget 4×");
		expect(screen.queryByTestId("dev-workflow-node-interrupted")).toBeNull();
	});

	it("counts only THIS node's interruptions, out of a feed that carries every node's", () => {
		// The panel is handed the whole run feed now. A sibling's restart is not this node's evidence.
		renderPanel(devWorkflowNodeRunDetail(), {
			events: [
				...interruptedEvents(1),
				devWorkflowRunEvent({ id: "other", sequence: 9, eventType: "node.interrupted", nodeRunId: "some-other-node" }),
			],
		});

		expect(screen.getByTestId("dev-workflow-node-interrupted").textContent).toBe("interrupted and re-dispatched 1×");
	});

	it("lists prior attempts from the event log, which is the only place they exist", () => {
		const nodeRunId = devWorkflowNodeRunDetail().id;
		renderPanel(
			devWorkflowNodeRunDetail({
				attempt: 2,
				maxAttempts: 3,
				inputTokens: 1200,
				outputTokens: 340,
				providerCalls: 4,
				toolCalls: 7,
				agentTurnMs: 40_000,
			}),
			{
				events: [
					devWorkflowRunEvent({
						id: "attached-1",
						sequence: 1,
						eventType: "worksession.attached",
						nodeRunId,
						detailJson: JSON.stringify({ workSessionId: sessionId, attempt: 1, sessionResumes: 0 }),
					}),
					devWorkflowRunEvent({
						id: "retry-1",
						sequence: 2,
						eventType: "node.retry.scheduled",
						nodeRunId,
						outcome: "provider-error",
						// The nine additive members the retry payload carries. They are the ONLY record of what the attempt
						// they close spent: the node-run row keeps the last attempt's numbers and overwrites the rest.
						detailJson: JSON.stringify({
							attempt: 1,
							inputTokens: 1000,
							outputTokens: 200,
							reasoningTokens: 50,
							estimatedInputTokens: 990,
							providerCalls: 2,
							toolCalls: 1,
							toolSchemaTokens: 100,
							agentTurnMs: 30_000,
							workSessionSteps: 2,
						}),
					}),
				],
			},
		);

		expect(screen.getByTestId("dev-workflow-node-attempt-1").textContent).toContain("provider-error");
		// Attempt 2 is the one running: the row exists because the node-run says so, with nothing claimed about it yet.
		expect(screen.getByTestId("dev-workflow-node-attempt-2")).toBeDefined();
		fireEvent.click(screen.getByTestId("dev-workflow-node-attempt-session-1"));
		expect(navigate).toHaveBeenCalledWith({ to: "/work-sessions/$sessionId", params: { sessionId } });

		// What the retried attempt cost, from its own event, and what the current one cost, from the node-run row.
		const first = screen.getByTestId("dev-workflow-node-attempt-cost-1").textContent ?? "";
		expect(first).toContain("Input tokens 1,000");
		expect(first).toContain("Output tokens 200");
		expect(first).toContain("Agent turns 30s");
		expect(screen.getByTestId("dev-workflow-node-attempt-cost-2").textContent).toContain("Input tokens 1,200");

		// `total = final + retries`, which is the number an operator cannot obtain from either source alone.
		const total = screen.getByTestId("dev-workflow-node-attempts-total").textContent ?? "";
		expect(total).toContain("Total across attempts");
		expect(total).toContain("Input tokens 2,200");
		expect(total).toContain("Output tokens 540");
		expect(total).toContain("Reasoning tokens 50");
		expect(total).toContain("Provider calls 6");
		expect(total).toContain("Tool calls 8");
		// Summed in milliseconds and formatted once: 30s on the retried attempt plus 40s on the current one.
		expect(total).toContain("Agent turns 1m 10s");
		expect(total).not.toContain("the real total is higher");
	});

	it("draws no total for a node that spends no tokens, so a Tool retry does not get a row of zeroes", () => {
		const nodeRunId = devWorkflowNodeRunDetail().id;
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Tool", attempt: 2, maxAttempts: 3 }), {
			events: [devWorkflowRunEvent({ id: "retry-tool", sequence: 2, eventType: "node.retry.scheduled", nodeRunId })],
		});

		expect(screen.queryByTestId("dev-workflow-node-attempts-total")).toBeNull();
	});

	it("calls the total partial when an earlier attempt was billed for rounds the provider never reported usage for", () => {
		// A context-window overflow ends the attempt after N provider calls and reports no usage for any of them, so the
		// payload carries calls and tools with null tokens. Adding only attempt 2 would print the last attempt's tokens
		// as if they were the whole bill.
		const nodeRunId = devWorkflowNodeRunDetail().id;
		renderPanel(devWorkflowNodeRunDetail({ attempt: 2, maxAttempts: 3, inputTokens: 900, outputTokens: 120, providerCalls: 3 }), {
			events: [
				devWorkflowRunEvent({
					id: "retry-overflow",
					sequence: 2,
					eventType: "node.retry.scheduled",
					nodeRunId,
					outcome: "context-overflow",
					detailJson: JSON.stringify({ attempt: 1, providerCalls: 10, toolCalls: 4, inputTokens: null, outputTokens: null }),
				}),
			],
		});

		const total = screen.getByTestId("dev-workflow-node-attempts-total").textContent ?? "";
		expect(total).toContain("Provider calls 13");
		expect(total).toContain("the real total is higher");
	});

	it("counts the last attempt in the total's honesty only once the node has settled", () => {
		// The overflow can hit the FINAL attempt too: the node ends Failed having paid for rounds nobody has token
		// counts for. Excluding the last row unconditionally would label that sum a complete total.
		const nodeRunId = devWorkflowNodeRunDetail().id;
		// Attempt 1 is fully recorded, so the last row's status is the only thing deciding the wording below.
		const events = [
			devWorkflowRunEvent({
				id: "retry-a",
				sequence: 2,
				eventType: "node.retry.scheduled",
				nodeRunId,
				detailJson: JSON.stringify({ attempt: 1, inputTokens: 400, providerCalls: 2 }),
			}),
		];
		renderPanel(devWorkflowNodeRunDetail({ status: "Failed", attempt: 2, maxAttempts: 3, providerCalls: 6, inputTokens: null }), {
			events,
		});
		expect(screen.getByTestId("dev-workflow-node-attempts-total").textContent).toContain("the real total is higher");

		// The same shape while the node is still working is not missing evidence, it is work not finished yet.
		cleanup();
		renderPanel(devWorkflowNodeRunDetail({ status: "Running", attempt: 2, maxAttempts: 3, providerCalls: 6, inputTokens: null }), {
			events,
		});
		expect(screen.getByTestId("dev-workflow-node-attempts-total").textContent).not.toContain("the real total is higher");
	});

	it("says the total is a floor when an earlier attempt's event never loaded, rather than printing a wrong number", () => {
		// A tail-anchored page set reaches attempt 2's retry but not attempt 1's, so attempt 1 recorded nothing. Adding
		// the two it CAN see and calling that the total would understate what the run paid, silently.
		const nodeRunId = devWorkflowNodeRunDetail().id;
		renderPanel(devWorkflowNodeRunDetail({ attempt: 3, maxAttempts: 3, inputTokens: 500, providerCalls: 1 }), {
			events: [
				devWorkflowRunEvent({
					id: "retry-2",
					sequence: 9,
					eventType: "node.retry.scheduled",
					nodeRunId,
					outcome: "provider-error",
					detailJson: JSON.stringify({ attempt: 2, inputTokens: 300, providerCalls: 2 }),
				}),
			],
		});

		expect(screen.queryByTestId("dev-workflow-node-attempt-cost-1")).toBeNull();
		expect(screen.getByTestId("dev-workflow-node-attempt-cost-2").textContent).toContain("Input tokens 300");
		const total = screen.getByTestId("dev-workflow-node-attempts-total").textContent ?? "";
		expect(total).toContain("Input tokens 800");
		expect(total).toContain("the real total is higher");
	});

	it("still offers the transcript of an attempt whose row the store wrote in PascalCase", () => {
		// The pre-FX-D spelling, which every existing run keeps for ever. Read only under the new one, the session id
		// is undefined and this link simply does not render — the failure this whole class of bug has: silence.
		const nodeRunId = devWorkflowNodeRunDetail().id;
		// Two attempts, because a one-row list renders nothing — the header already says "attempt 1 of N".
		renderPanel(devWorkflowNodeRunDetail({ attempt: 2, maxAttempts: 3 }), {
			events: [
				devWorkflowRunEvent({
					id: "attached-legacy",
					sequence: 1,
					eventType: "worksession.attached",
					nodeRunId,
					detailJson: JSON.stringify({ WorkSessionId: sessionId, Attempt: 1, SessionResumes: 0 }),
				}),
				devWorkflowRunEvent({ id: "retry-legacy", sequence: 2, eventType: "node.retry.scheduled", nodeRunId }),
			],
		});

		fireEvent.click(screen.getByTestId("dev-workflow-node-attempt-session-1"));
		expect(navigate).toHaveBeenCalledWith({ to: "/work-sessions/$sessionId", params: { sessionId } });
	});

	it("does not blame an unrelated fix loop for a node's own retry", () => {
		// A same-node retry emits `node.retry.scheduled` with NO routed event of its own, so the newest routed event
		// anywhere in the run sits at-or-before it — under C2's N subtrees that is the ordinary case, not a corner.
		// Reading it would name a node that has nothing to do with this reset.
		const nodeRunId = devWorkflowNodeRunDetail().id;
		renderPanel(devWorkflowNodeRunDetail({ nodeKey: "implement", attempt: 2 }), {
			run: devWorkflowRun({
				nodes: [devWorkflowNodeRunSummary({ id: "node-validate", nodeKey: "validate", label: "Validate the patch" })],
			}),
			events: [
				devWorkflowRunEvent({
					id: "routed-elsewhere",
					sequence: 3,
					eventType: "node.retry.routed",
					nodeRunId: "node-validate",
					detailJson: JSON.stringify({ from: "validate", to: "document" }),
				}),
				devWorkflowRunEvent({ id: "reset", sequence: 5, eventType: "node.retry.scheduled", nodeRunId }),
			],
		});

		expect(screen.queryByTestId("dev-workflow-node-cascade-rerun")).toBeNull();
	});

	it("says why a completed node is running again, rather than letting it silently un-complete", () => {
		const nodeRunId = devWorkflowNodeRunDetail().id;
		renderPanel(devWorkflowNodeRunDetail({ nodeKey: "implement", attempt: 2 }), {
			run: devWorkflowRun({
				nodes: [devWorkflowNodeRunSummary({ id: "node-validate", nodeKey: "validate", label: "Validate the patch" })],
			}),
			events: [
				devWorkflowRunEvent({
					id: "routed",
					sequence: 4,
					eventType: "node.retry.routed",
					nodeRunId: "node-validate",
					detailJson: JSON.stringify({ from: "validate", to: "implement" }),
				}),
				devWorkflowRunEvent({ id: "reset", sequence: 5, eventType: "node.retry.scheduled", nodeRunId }),
			],
		});

		expect(screen.getByTestId("dev-workflow-node-cascade-rerun").textContent).toContain("Validate the patch");
	});

	it("does not call a node's own retry a cascade — that is just this node trying again", () => {
		const nodeRunId = devWorkflowNodeRunDetail().id;
		renderPanel(devWorkflowNodeRunDetail({ nodeKey: "implement", attempt: 2 }), {
			events: [
				devWorkflowRunEvent({
					id: "routed",
					sequence: 4,
					eventType: "node.retry.routed",
					nodeRunId,
					detailJson: JSON.stringify({ from: "implement", to: "implement" }),
				}),
				devWorkflowRunEvent({ id: "reset", sequence: 5, eventType: "node.retry.scheduled", nodeRunId }),
			],
		});

		expect(screen.queryByTestId("dev-workflow-node-cascade-rerun")).toBeNull();
	});

	it("explains a failed node with its failure class and sanitized reason", () => {
		renderPanel(
			devWorkflowNodeRunDetail({ status: "Failed", failureClass: "Timeout", terminalReason: "no step in 900s", completedAtUtc: 2 }),
		);

		const failure = screen.getByTestId("dev-workflow-node-failure");
		expect(failure.textContent).toContain("Timed out");
		expect(failure.textContent).toContain("no step in 900s");
	});

	it("falls back to a plain sentence for a failure class a newer server invented", () => {
		renderPanel(devWorkflowNodeRunDetail({ status: "Failed", failureClass: "QuantumFluctuation" }));

		expect(screen.getByTestId("dev-workflow-node-failure").textContent).toContain("The node failed");
		expect(screen.getByTestId("dev-workflow-node-failure").textContent).not.toContain("QuantumFluctuation");
	});

	it("sends a tool node's validation report to the artifacts tab instead of re-rendering it here", () => {
		const { onShowArtifacts } = renderPanel(
			devWorkflowNodeRunDetail({ nodeType: "Tool", primaryArtifactId: "99999999-9999-4999-8999-999999999999" }),
		);

		fireEvent.click(screen.getByTestId("dev-workflow-node-tool-report"));

		expect(onShowArtifacts).toHaveBeenCalledTimes(1);
	});

	it("shows a DevTask node's task id and links to Dev Mode, which owns the evidence chain", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "DevTask", developmentTaskId: "task-7" }));

		expect(screen.getByTestId("dev-workflow-node-devtask-id").textContent).toBe("task-7");
		fireEvent.click(screen.getByTestId("dev-workflow-node-development-link"));
		expect(navigate).toHaveBeenCalledWith({ to: "/development" });
	});

	it("renders a structural node with no link-outs at all", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Join", label: "Join", workSessionId: null }));

		expect(screen.getByTestId("dev-workflow-node-panel-label").textContent).toBe("Join");
		expect(screen.queryByTestId("dev-workflow-node-agent")).toBeNull();
		expect(screen.queryByTestId("dev-workflow-node-tool")).toBeNull();
		expect(screen.queryByTestId("dev-workflow-node-devtask")).toBeNull();
	});

	it("shows a Join's dependencies split into satisfied and outstanding, mirroring the state machine's edge rule", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Join", nodeKey: "integrate", label: "Integrate" }), {
			run: devWorkflowRun({
				graph: {
					schemaVersion: 1,
					nodes: [],
					edges: [
						{ from: "implement#0", to: "integrate" },
						{ from: "implement#1", to: "integrate" },
					],
				},
				nodes: [
					devWorkflowNodeRunSummary({ id: "n0", nodeKey: "integrate", waitingOnNodeKeys: ["implement#1"] }),
					devWorkflowNodeRunSummary({ id: "n1", nodeKey: "implement#0", label: "Slice one", status: "Succeeded" }),
					devWorkflowNodeRunSummary({ id: "n2", nodeKey: "implement#1", label: "Slice two", status: "Running" }),
				],
			}),
		});

		expect(screen.getByTestId("dev-workflow-node-dependency-implement#0").textContent).toContain("satisfied");
		expect(screen.getByTestId("dev-workflow-node-dependency-implement#1").textContent).toContain("outstanding");
		expect(screen.getByTestId("dev-workflow-node-dependency-implement#0").textContent).toContain("Slice one");
	});

	it("badges a DEAD inbound branch dead rather than satisfied, which is what the join will do with it", () => {
		// LIVE-3 P2: the verdict came off `waitingOnNodeKeys`, which the runtime sends only while the join is Pending and
		// which drops every SETTLED source — so a Skipped branch arrived as "not waited on" and read SATISFIED. Under
		// `DevWorkflowStateMachine.EdgeState` a Failed or Cancelled source makes the edge DEAD, and that dead edge is
		// precisely why an `All` join skips. The panel said the opposite of what was about to happen.
		//
		// C1 then split the third case back out: a Skipped source is WAIVED, because a skip a person chose is not a
		// reason to throw away what its siblings carried. WHICH skip that is comes off the row as `skipWaived`, which
		// the server computes: the ancestor that decides it need not be in this list at all.
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Join", nodeKey: "join", label: "Join" }), {
			run: devWorkflowRun({
				graph: {
					schemaVersion: 1,
					nodes: [{ nodeKey: "join", nodeType: "Join", label: "Join" }],
					edges: [
						{ from: "landed", to: "join" },
						{ from: "skipped", to: "join" },
						{ from: "failed", to: "join" },
						{ from: "cancelled", to: "join" },
						{ from: "live", to: "join" },
					],
				},
				nodes: [
					devWorkflowNodeRunSummary({ id: "n0", nodeKey: "join" }),
					devWorkflowNodeRunSummary({ id: "n1", nodeKey: "landed", label: "Landed", status: "Succeeded" }),
					devWorkflowNodeRunSummary({ id: "n2", nodeKey: "skipped", label: "Skipped one", status: "Skipped", skipWaived: true }),
					devWorkflowNodeRunSummary({ id: "n3", nodeKey: "failed", label: "Failed one", status: "Failed" }),
					devWorkflowNodeRunSummary({ id: "n4", nodeKey: "cancelled", label: "Cancelled one", status: "Cancelled" }),
					devWorkflowNodeRunSummary({ id: "n5", nodeKey: "live", label: "Live one", status: "Running" }),
				],
			}),
		});

		for (const key of ["failed", "cancelled"]) {
			const row = screen.getByTestId(`dev-workflow-node-dependency-${key}`).textContent ?? "";
			// `All` is the default policy, so the wording names the consequence: the join skips.
			expect(row).toContain("dead — the join skips once nothing is pending");
			expect(row).not.toContain("satisfied");
			expect(row).not.toContain("outstanding");
		}
		const excused = screen.getByTestId("dev-workflow-node-dependency-skipped").textContent ?? "";
		expect(excused).toContain("skipped — the join carries on if a sibling succeeded");
		expect(excused).not.toContain("satisfied");
		expect(excused).not.toContain("outstanding");
		expect(screen.getByTestId("dev-workflow-node-dependency-landed").textContent).toContain("satisfied");
		// Unsettled is a WAIT, not a verdict — the same answer `EdgeState` gives for a source that has not landed.
		expect(screen.getByTestId("dev-workflow-node-dependency-live").textContent).toContain("outstanding");
	});

	it("does not tell an Any join it will skip: there a dead branch is ignored while a sibling landed", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Join", nodeKey: "join", label: "Join" }), {
			run: devWorkflowRun({
				graph: {
					schemaVersion: 1,
					nodes: [{ nodeKey: "join", nodeType: "Join", label: "Join", joinPolicy: "Any" }],
					edges: [
						{ from: "landed", to: "join" },
						{ from: "skipped", to: "join" },
					],
				},
				nodes: [
					devWorkflowNodeRunSummary({ id: "n0", nodeKey: "join" }),
					devWorkflowNodeRunSummary({ id: "n1", nodeKey: "landed", label: "Landed", status: "Succeeded" }),
					devWorkflowNodeRunSummary({ id: "n2", nodeKey: "skipped", label: "Skipped one", status: "Skipped" }),
				],
			}),
		});

		const row = screen.getByTestId("dev-workflow-node-dependency-skipped").textContent ?? "";
		expect(row).toContain("dead — this branch will not carry the join");
		expect(row).not.toContain("skips once nothing is pending");
	});

	it("shows a Gate's branches with their stored conditions, and marks the one the run actually took", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Gate", nodeKey: "verdict", label: "Verdict" }), {
			run: devWorkflowRun({
				graph: {
					schemaVersion: 1,
					nodes: [],
					edges: [
						{ from: "verdict", to: "ship", condition: { path: "$.passed", op: "eq", value: true } },
						{ from: "verdict", to: "fix", condition: { path: "$.passed", op: "eq", value: false } },
					],
				},
				nodes: [
					devWorkflowNodeRunSummary({ id: "n0", nodeKey: "verdict" }),
					devWorkflowNodeRunSummary({ id: "n1", nodeKey: "ship", label: "Ship it", status: "Running" }),
					devWorkflowNodeRunSummary({ id: "n2", nodeKey: "fix", label: "Fix it", status: "Pending" }),
				],
			}),
		});

		// The condition is rendered as stored. A client paraphrase the runtime evaluates differently is worse than none.
		expect(screen.getByTestId("dev-workflow-node-branch-condition-ship").textContent).toBe("$.passed eq true");
		// There is no conditionResult field on the wire; the taken branch is the successor the run actually entered.
		expect(screen.getByTestId("dev-workflow-node-branch-taken-ship")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-node-branch-taken-fix")).toBeNull();
	});

	it("does not call a SKIPPED branch taken — which is what the branch not taken actually looks like", () => {
		// A gate that has settled leaves its dead branch `Skipped`, not `Pending`: the state machine reads the dead edge
		// as Admission.Skip and the dispatcher writes the row. So "not Pending" as a proxy for "taken" badged BOTH
		// branches of every decided gate, which is a lie on the one surface that answers which way the run went.
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Gate", nodeKey: "verdict", label: "Verdict" }), {
			run: devWorkflowRun({
				graph: {
					schemaVersion: 1,
					nodes: [],
					edges: [
						{ from: "verdict", to: "ship", condition: { path: "$.passed", op: "eq", value: true } },
						{ from: "verdict", to: "fix", condition: { path: "$.passed", op: "eq", value: false } },
						{ from: "verdict", to: "audit", condition: { path: "$.passed", op: "eq", value: false } },
					],
				},
				nodes: [
					devWorkflowNodeRunSummary({ id: "n0", nodeKey: "verdict", status: "Succeeded" }),
					devWorkflowNodeRunSummary({ id: "n1", nodeKey: "ship", label: "Ship it", status: "Succeeded" }),
					devWorkflowNodeRunSummary({ id: "n2", nodeKey: "fix", label: "Fix it", status: "Skipped" }),
					devWorkflowNodeRunSummary({ id: "n3", nodeKey: "audit", label: "Audit it", status: "Cancelled" }),
				],
			}),
		});

		expect(screen.getByTestId("dev-workflow-node-branch-taken-ship")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-node-branch-taken-fix")).toBeNull();
		// A run cancelled before a branch ran did not choose that branch either.
		expect(screen.queryByTestId("dev-workflow-node-branch-taken-audit")).toBeNull();
	});

	it("names a row-less TEMPLATE dependency as a template rather than calling it satisfied", () => {
		// A template key never gets a row — its children get them — so a row-less template is neither satisfied nor dead
		// nor waited on; before FX-F it rendered SATISFIED, met by a node that had not run and could not run.
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Join", nodeKey: "join", label: "Join" }), {
			run: devWorkflowRun({
				graph: {
					schemaVersion: 1,
					nodes: [
						{
							nodeKey: "decompose",
							nodeType: "Agent",
							label: "Decompose",
							materialization: { templateNodeKey: "implement", artifactKind: "TaskPackage", joinNodeKey: "join" },
						},
						{ nodeKey: "implement", nodeType: "DevTask", label: "Implement", isTemplate: true },
						{ nodeKey: "validate", nodeType: "Tool", label: "Validate", isTemplate: true },
						{ nodeKey: "plan", nodeType: "Agent", label: "Plan" },
						{ nodeKey: "join", nodeType: "Join", label: "Join" },
					],
					edges: [
						{ from: "implement", to: "validate" },
						{ from: "validate", to: "join" },
						{ from: "plan", to: "join" },
					],
				},
				nodes: [
					devWorkflowNodeRunSummary({ id: "n0", nodeKey: "join", waitingOnNodeKeys: ["plan"] }),
					devWorkflowNodeRunSummary({ id: "n1", nodeKey: "plan", label: "Plan", status: "Running" }),
				],
			}),
		});

		// `validate` is inside the template subtree, which the SERVER declares on the pinned graph as `isTemplate`.
		expect(screen.getByTestId("dev-workflow-node-dependency-validate").textContent).toContain("template");
		expect(screen.getByTestId("dev-workflow-node-dependency-validate").textContent).not.toContain("satisfied");
		// A real dependency the runtime IS waiting on is unaffected.
		expect(screen.getByTestId("dev-workflow-node-dependency-plan").textContent).toContain("outstanding");
	});

	it("shows a settled HumanGate's branches, because the seeded template's gates are the only gates it has", () => {
		// `feature-development-v1` ships only HumanGates, so a decision panel with no branch view meant the branch a
		// run actually took was unreachable from the one template that matters.
		renderPanel(
			devWorkflowNodeRunDetail({
				nodeType: "HumanGate",
				nodeKey: "planapproval",
				label: "Approve the plan",
				status: "Succeeded",
				pendingDecisionKind: null,
			}),
			{
				run: devWorkflowRun({
					graph: {
						schemaVersion: 1,
						nodes: [],
						edges: [
							{ from: "planapproval", to: "decompose", condition: { path: "$.decision", op: "eq", value: "Approve" } },
							{ from: "planapproval", to: "replan", condition: { path: "$.decision", op: "eq", value: "Reject" } },
						],
					},
					nodes: [
						devWorkflowNodeRunSummary({ id: "n0", nodeKey: "planapproval", status: "Succeeded" }),
						devWorkflowNodeRunSummary({ id: "n1", nodeKey: "decompose", label: "Decompose", status: "Running" }),
						devWorkflowNodeRunSummary({ id: "n2", nodeKey: "replan", label: "Re-plan", status: "Skipped" }),
					],
				}),
			},
		);

		expect(screen.getByTestId("dev-workflow-node-branch-condition-decompose").textContent).toBe('$.decision eq "Approve"');
		// The same untaken semantics as a `Gate`: the loser is Skipped, and Skipped is not taken.
		expect(screen.getByTestId("dev-workflow-node-branch-taken-decompose")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-node-branch-taken-replan")).toBeNull();
		// A HumanGate is not a Parallel: its successors are alternatives, not concurrent work.
		expect(screen.getByTestId("dev-workflow-node-structural-branches").textContent).toContain("Branches");
	});

	it("shows an OPEN HumanGate no branches at all, because nothing has been decided to show", () => {
		renderPanel(
			devWorkflowNodeRunDetail({
				nodeType: "HumanGate",
				nodeKey: "planapproval",
				label: "Approve the plan",
				status: "WaitingForApproval",
				pendingDecisionKind: "Approve",
				allowedDecisions: ["Approve", "Reject"],
			}),
			{
				run: devWorkflowRun({
					graph: { schemaVersion: 1, nodes: [], edges: [{ from: "planapproval", to: "decompose" }] },
					nodes: [
						devWorkflowNodeRunSummary({ id: "n0", nodeKey: "planapproval", status: "WaitingForApproval" }),
						devWorkflowNodeRunSummary({ id: "n1", nodeKey: "decompose", label: "Decompose", status: "Pending" }),
					],
				}),
			},
		);

		// Every successor is still Pending, so a branch list here would name no branch — the decision controls are the
		// whole surface until the gate is answered.
		expect(screen.getByTestId("dev-workflow-gate-panel")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-node-structural")).toBeNull();
	});

	it("still lists a branch whose node has not been created yet, rather than shrinking the graph", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Parallel", nodeKey: "fanout", label: "Fan out" }), {
			run: devWorkflowRun({
				graph: { schemaVersion: 1, nodes: [], edges: [{ from: "fanout", to: "implement" }] },
				nodes: [devWorkflowNodeRunSummary({ id: "n0", nodeKey: "fanout" })],
			}),
		});

		// A materialization template has no node-run row until its children exist; hiding it would be a smaller graph.
		expect(screen.getByTestId("dev-workflow-node-branch-implement").textContent).toContain("not created yet");
	});

	it("renders inputJson as raw text, because nothing parses it in v1", () => {
		renderPanel(devWorkflowNodeRunDetail({ inputJson: '{"workItemRequest":"Compare the options"}' }));

		expect(screen.getByTestId("dev-workflow-node-input").textContent).toBe('{"workItemRequest":"Compare the options"}');
	});

	it("names the rule sets that were baked into this node run's objective, with the revision it used", () => {
		renderPanel(
			devWorkflowNodeRunDetail({
				appliedRuleSets: [{ id: "rs-1", name: "House style", contentSha256: "abcdef1234567890", currentContentSha256: "abcdef1234567890" }],
			}),
		);

		const row = screen.getByTestId("dev-workflow-node-rule-set-rs-1");
		expect(row.textContent).toContain("House style");
		expect(row.textContent).toContain("abcdef12");
		expect(screen.queryByTestId("dev-workflow-node-rule-set-edited-rs-1")).toBeNull();
	});

	it("badges a rule set whose body moved on since the run used it, and one that has been deleted", () => {
		renderPanel(
			devWorkflowNodeRunDetail({
				appliedRuleSets: [
					{ id: "rs-1", name: "House style", contentSha256: "aaaa", currentContentSha256: "bbbb" },
					{ id: "rs-2", name: "Gone", contentSha256: "cccc", currentContentSha256: null },
				],
			}),
		);

		expect(screen.getByTestId("dev-workflow-node-rule-set-edited-rs-1")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-node-rule-set-deleted-rs-2")).toBeDefined();
	});

	it("renders no rule-set section at all when the node run applied none", () => {
		renderPanel(devWorkflowNodeRunDetail({ appliedRuleSets: [] }));

		expect(screen.queryByTestId("dev-workflow-node-rule-sets")).toBeNull();
	});

	it("renders the cost section with the thirteen columns, the tool chips and the route", () => {
		renderPanel(
			devWorkflowNodeRunDetail({
				status: "Succeeded",
				queuedAtUtc: 1_000,
				startedAtUtc: 3_000,
				completedAtUtc: 63_000,
				inputTokens: 1200,
				outputTokens: 340,
				reasoningTokens: 90,
				estimatedInputTokens: 1150,
				providerCalls: 4,
				toolCalls: 7,
				toolSchemaTokens: 320,
				toolNames: ["read_document", "search_web", "…"],
				agentTurnMs: 40_000,
				modelReadinessMs: 12_000,
				servedModelName: "qwen3-27b-instruct-q4",
				workSessionSteps: 3,
				route: { satisfied: ["approval"], dead: ["fallback"], waived: ["excused-leaf"], gateAnswer: "Approve", truncated: true },
			}),
		);

		expect(screen.getByTestId("dev-workflow-node-cost-input").textContent).toBe("1,200");
		expect(screen.getByTestId("dev-workflow-node-cost-output").textContent).toBe("340");
		expect(screen.getByTestId("dev-workflow-node-cost-reasoning").textContent).toBe("90");
		expect(screen.getByTestId("dev-workflow-node-cost-provider-calls").textContent).toBe("4");
		expect(screen.getByTestId("dev-workflow-node-cost-tool-calls").textContent).toBe("7");
		expect(screen.getByTestId("dev-workflow-node-cost-schema-tokens").textContent).toBe("320");
		expect(screen.getByTestId("dev-workflow-node-cost-steps").textContent).toBe("3");
		expect(screen.getByTestId("dev-workflow-node-cost-served-model").textContent).toBe("qwen3-27b-instruct-q4");

		// The four durations: queued, total, inside agent turns, and what is left over outside those turns.
		expect(screen.getByTestId("dev-workflow-node-cost-queued").textContent).toBe("2s");
		expect(screen.getByTestId("dev-workflow-node-cost-ran").textContent).toBe("1m 00s");
		expect(screen.getByTestId("dev-workflow-node-cost-turn-time").textContent).toBe("40s");
		// Part OF the agent turns, not beside them: the wait for llama-server to launch and load the model.
		expect(screen.getByTestId("dev-workflow-node-cost-model-readiness").textContent).toBe("12s");
		expect(screen.getByTestId("dev-workflow-node-cost-outside-turn-time").textContent).toBe("20s");

		// The estimate is suppressed while the real count is present: two numbers for one quantity invite addition.
		expect(screen.queryByTestId("dev-workflow-node-cost-estimated")).toBeNull();

		// The collector closes a trimmed list with "…". It is a marker, not a tool, so it must not be drawn as a chip.
		const chips = screen.getByTestId("dev-workflow-node-cost-tool-names");
		expect(chips.textContent).toContain("read_document");
		expect(chips.textContent).toContain("search_web");
		expect(screen.getByTestId("dev-workflow-node-cost-tool-names-truncated").textContent).toBe("…and more");

		expect(screen.getByTestId("dev-workflow-node-cost-route-satisfied").textContent).toBe("satisfied → approval");
		expect(screen.getByTestId("dev-workflow-node-cost-route-dead").textContent).toBe("not taken → fallback");
		// Its own row, because a waived edge is neither of the other two: it did not admit this successor and it did
		// not kill it either. Folding it into one of them is what would make the panel disagree with the runtime.
		expect(screen.getByTestId("dev-workflow-node-cost-route-waived").textContent).toBe("excused → excused-leaf");
		expect(screen.getByTestId("dev-workflow-node-cost-route-gate-answer").textContent).toContain("Approve");
		// A shortened route MUST say so, or a two-key list reads as the whole route.
		expect(screen.getByTestId("dev-workflow-node-cost-route-truncated")).toBeDefined();
	});

	it("draws no excused row for an ordinary route, so an empty bucket is not read as a claim", () => {
		renderPanel(
			devWorkflowNodeRunDetail({
				status: "Succeeded",
				route: { satisfied: ["approval"], dead: [], waived: [], gateAnswer: null, truncated: false },
			}),
		);

		expect(screen.getByTestId("dev-workflow-node-cost-route-satisfied").textContent).toBe("satisfied → approval");
		expect(screen.queryByTestId("dev-workflow-node-cost-route-waived")).toBeNull();
	});

	// A cloud-served node, and one that reused an already-loaded model, warmed nothing. A "0s" row would claim the
	// launch was instant instead of saying it never happened.
	it("draws no model-readiness row when no turn warmed a local runtime", () => {
		renderPanel(devWorkflowNodeRunDetail({ status: "Succeeded", agentTurnMs: 40_000, modelReadinessMs: null }));

		expect(screen.getByTestId("dev-workflow-node-cost-turn-time").textContent).toBe("40s");
		expect(screen.queryByTestId("dev-workflow-node-cost-model-readiness")).toBeNull();
	});

	// Readiness alone is still a measurement, so the section must show the row rather than "nothing was recorded".
	it("counts a lone model-readiness reading as something recorded", () => {
		renderPanel(
			devWorkflowNodeRunDetail({
				status: "Succeeded",
				queuedAtUtc: null,
				startedAtUtc: null,
				completedAtUtc: null,
				modelReadinessMs: 12_000,
			}),
		);

		expect(screen.getByTestId("dev-workflow-node-cost-model-readiness").textContent).toBe("12s");
		expect(screen.queryByTestId("dev-workflow-node-cost-none")).toBeNull();
	});

	it("shows the estimate only where the real input count is missing", () => {
		renderPanel(devWorkflowNodeRunDetail({ status: "Succeeded", inputTokens: null, estimatedInputTokens: 1150 }));

		expect(screen.getByTestId("dev-workflow-node-cost-estimated").textContent).toBe("1,150");
	});

	it("renders no cost section at all for a node run that recorded nothing", () => {
		renderPanel(devWorkflowNodeRunDetail({ status: "Pending", queuedAtUtc: null, startedAtUtc: null, completedAtUtc: null }));

		expect(screen.queryByTestId("dev-workflow-node-cost")).toBeNull();
	});

	it("names the failure in the cross-unit vocabulary beside the runtime's own class", () => {
		renderPanel(devWorkflowNodeRunDetail({ status: "Failed", failureClass: "ToolCommandFailed", failureClassGroup: "ToolOrCommand" }));

		expect(screen.getByTestId("dev-workflow-node-failure-group").textContent).toBe("Tool or command");
	});

	it("clamps the attempt maximum up to the attempt, so a server from before the widening never reads 'attempt 4 of 3'", () => {
		renderPanel(devWorkflowNodeRunDetail({ attempt: 4, maxAttempts: 3 }));

		expect(screen.getByTestId("dev-workflow-node-panel-label").closest("[data-testid='dev-workflow-node-panel']")?.textContent).toContain(
			"attempt 4 of 4",
		);
	});

	it("names the declared cap and the capacity an operator added, not who started this attempt", () => {
		renderPanel(devWorkflowNodeRunDetail({ attempt: 4, maxAttempts: 4, operatorRetries: 1 }));

		expect(screen.getByTestId("dev-workflow-node-panel-label").closest("[data-testid='dev-workflow-node-panel']")?.textContent).toContain(
			"attempt 4 of 4 (cap 3, +1 from an operator retry)",
		);
	});

	it("sums the capacity once more than one retry widened the cap", () => {
		renderPanel(devWorkflowNodeRunDetail({ attempt: 5, maxAttempts: 5, operatorRetries: 2 }));

		expect(screen.getByTestId("dev-workflow-node-panel-label").closest("[data-testid='dev-workflow-node-panel']")?.textContent).toContain(
			"attempt 5 of 5 (cap 3, +2 from operator retries)",
		);
	});
});
