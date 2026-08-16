// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ToolCallCard } from "@/features/chat/components/ToolCallCard";
import type { ChatToolPart } from "@/features/chat/models/ChatModels";
import type { ToolCatalogEntry } from "@/features/tools/models/ToolCatalogModels";

// The Approve/Deny controls fire the generated resolve-approval mutation; stub its mutationFn so the click wiring can
// be asserted without a backend. `withResponseValidation` (which the card composes over the mutation) preserves the
// mutationFn, so the spy still receives the mutate variables. Only this one export is overridden.
const resolveApprovalSpy = vi.fn().mockResolvedValue({ requestId: "approval-42", approved: true });
vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	resolveToolApprovalMutation: () => ({ mutationFn: resolveApprovalSpy }),
}));

// The card resolves the tool's risk class AND its session-scope eligibility from the shared catalog; mock it so both
// are deterministic and no real fetch fires. get_time is a ReadLocal auto-executing built-in and stands in for a
// session-scope-eligible tool (the flag is what the card branches on, not the tool's identity); mcp__files__write_file
// is the ineligible case; anything absent falls back to fail-closed Unknown + the pre-catalog session-button fallback.
const { useToolCatalogMock } = vi.hoisted(() => ({ useToolCatalogMock: vi.fn() }));
vi.mock("@/features/tools/queries/useToolCatalog", () => ({ useToolCatalog: useToolCatalogMock }));

const chatCatalog: ToolCatalogEntry[] = [
	{
		name: "get_time",
		description: "Returns the current time.",
		requiresApproval: false,
		source: { kind: "builtin", serverSlug: null },
		category: "ReadLocal",
		effectiveRequiresApproval: false,
		sessionScopeEligible: true,
	},
	{
		name: "mcp__files__write_file",
		description: "Writes a file.",
		requiresApproval: true,
		source: { kind: "mcp", serverSlug: "files" },
		category: "WriteExecute",
		effectiveRequiresApproval: true,
		sessionScopeEligible: false,
	},
];

function renderWithProviders(ui: ReactElement) {
	const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider>{ui}</MantineProvider>
		</QueryClientProvider>,
	);
}

function toolPart(overrides: Partial<ChatToolPart> = {}): ChatToolPart {
	return { kind: "tool", id: "call-1", sequence: 1, name: "get_time", state: "received", ...overrides };
}

describe("ToolCallCard", () => {
	beforeEach(() => {
		resolveApprovalSpy.mockClear();
		useToolCatalogMock.mockReturnValue({ data: chatCatalog, isLoading: false, error: null });
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
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("badges the tool's category from the catalog (ReadLocal auto-executes)", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "received", result: "ok" })} />);

		const badge = screen.getByTestId("tool-category-badge-ReadLocal");
		expect(badge.textContent).toContain("read-only");
		expect(badge.getAttribute("data-requires-approval")).toBe("false");
	});

	it("badges a tool absent from the catalog as Unknown (fail-closed to approval)", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ name: "mystery_tool", state: "waiting" })} />);

		const badge = screen.getByTestId("tool-category-badge-Unknown");
		expect(badge.textContent).toContain("uncategorized");
		expect(badge.getAttribute("data-requires-approval")).toBe("true");
	});

	it("shows the tool name and a live label while requesting", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "requesting", args: '{"tz":"utc"}' })} />);

		const card = screen.getByTestId("chat-tool-call-card-get_time");
		expect(card.getAttribute("data-state")).toBe("requesting");
		expect(screen.getByText("get_time")).toBeTruthy();
		expect(screen.getByText("live")).toBeTruthy();
		// Args render even while live so the call is legible before the result arrives.
		expect(card.textContent).toContain("tz");
	});

	it("renders the result body inline once received", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "received", result: '{"time":"12:00"}' })} />);

		const result = screen.getByTestId("chat-tool-call-result-get_time");
		expect(result.textContent).toContain("12:00");
		expect(screen.getByText("Result")).toBeTruthy();
	});

	it("pretty-prints JSON args and passes non-JSON through unchanged", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "received", args: '{"a":1}', result: "plain text" })} />);

		// JSON args are indented across lines; the plain result is shown verbatim inside the code block.
		expect(screen.getByTestId("chat-tool-call-result-get_time").textContent).toContain("plain text");
		expect(screen.getByText("Arguments")).toBeTruthy();
	});

	it("uses error styling and label on a failed tool call", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "failed", result: "boom" })} />);

		const card = screen.getByTestId("chat-tool-call-card-get_time");
		expect(card.getAttribute("data-state")).toBe("failed");
		expect(screen.getByText("Error")).toBeTruthy();
		expect(screen.getByTestId("chat-tool-call-result-get_time").textContent).toContain("boom");
	});

	it("surfaces an approval indicator when the tool requires approval", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "waiting", requiresApproval: true })} />);

		expect(screen.getByTestId("chat-tool-call-approval-get_time")).toBeTruthy();
	});

	it("shows a muted '(no output)' affordance when a received tool call has an empty result", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "received", result: undefined })} />);

		expect(screen.getByTestId("chat-tool-call-no-output-get_time")).toBeTruthy();
		// The result body must not render alongside the no-output notice.
		expect(screen.queryByTestId("chat-tool-call-result-get_time")).toBeNull();
	});

	it("does not show the no-output affordance while the tool is still in flight", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "requesting", result: undefined })} />);

		expect(screen.queryByTestId("chat-tool-call-no-output-get_time")).toBeNull();
	});

	it("renders Approve/Deny controls while awaiting an approval decision", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "waiting", requiresApproval: true, pendingApprovalRequestId: "approval-42" })} />);

		expect(screen.getByTestId("chat-tool-call-approve-get_time")).toBeTruthy();
		expect(screen.getByTestId("chat-tool-call-approve-session-get_time")).toBeTruthy();
		expect(screen.getByTestId("chat-tool-call-deny-get_time")).toBeTruthy();
	});

	it("withholds the session button for a tool the node cannot remember a session decision for", () => {
		// The honesty fix: an MCP tool (like run_in_agent_home, a Parameterized custom tool, or anything at all while the
		// operator's always-prompt switch is on) can never carry a session memo, so offering the button meant the click
		// silently degraded to a plain "Once". Approve/Deny stay — only the promise the node cannot keep is removed.
		renderWithProviders(
			<ToolCallCard
				part={toolPart({ name: "mcp__files__write_file", state: "waiting", requiresApproval: true, pendingApprovalRequestId: "approval-42" })}
			/>,
		);

		expect(screen.getByTestId("chat-tool-call-approve-mcp__files__write_file")).toBeTruthy();
		expect(screen.getByTestId("chat-tool-call-deny-mcp__files__write_file")).toBeTruthy();
		expect(screen.queryByTestId("chat-tool-call-approve-session-mcp__files__write_file")).toBeNull();
	});

	it("prefers the backend per-request flag over the catalog when deciding the session button", () => {
		// The catalog answers at tool-identity level and does not carry the MAF skill tools at all, so run_skill_script and
		// imported skills kept offering a session scope the node never honours. The runner publishes its own answer with
		// the approval request, and it wins in both directions.
		renderWithProviders(
			<ToolCallCard
				part={toolPart({
					name: "run_skill_script",
					state: "waiting",
					requiresApproval: true,
					pendingApprovalRequestId: "approval-42",
					pendingApprovalSessionScopeEligible: false,
				})}
			/>,
		);

		expect(screen.getByTestId("chat-tool-call-approve-run_skill_script")).toBeTruthy();
		expect(screen.queryByTestId("chat-tool-call-approve-session-run_skill_script")).toBeNull();
	});

	it("offers the session button when the backend says this request is eligible even if the catalog says otherwise", () => {
		renderWithProviders(
			<ToolCallCard
				part={toolPart({
					name: "mcp__files__write_file",
					state: "waiting",
					requiresApproval: true,
					pendingApprovalRequestId: "approval-42",
					pendingApprovalSessionScopeEligible: true,
				})}
			/>,
		);

		expect(screen.getByTestId("chat-tool-call-approve-session-mcp__files__write_file")).toBeTruthy();
	});

	it("keeps the session button for a tool absent from the catalog", () => {
		// The MAF skill tools (load_skill / read_skill_resource) are per-agent, not node-level, so they never appear in
		// the node tool catalog — and they ARE the original session-scope case. An unknown entry must therefore keep the
		// pre-catalog fallback of offering the button rather than fail closed and remove a control that works.
		renderWithProviders(
			<ToolCallCard part={toolPart({ name: "load_skill", state: "waiting", requiresApproval: true, pendingApprovalRequestId: "approval-42" })} />,
		);

		expect(screen.getByTestId("chat-tool-call-approve-session-load_skill")).toBeTruthy();
	});

	it("does not render approval controls when there is no pending approval request id", () => {
		// A shielded tool that is not (yet) awaiting a decision shows no Approve/Deny controls.
		renderWithProviders(<ToolCallCard part={toolPart({ state: "waiting", requiresApproval: true })} />);

		expect(screen.queryByTestId("chat-tool-call-approve-get_time")).toBeNull();
		expect(screen.queryByTestId("chat-tool-call-deny-get_time")).toBeNull();
	});

	it("does not render approval controls once the tool has resolved", () => {
		// A stale pendingApprovalRequestId on a received card must not resurrect the controls.
		renderWithProviders(<ToolCallCard part={toolPart({ state: "received", result: "ok", pendingApprovalRequestId: "approval-42" })} />);

		expect(screen.queryByTestId("chat-tool-call-approve-get_time")).toBeNull();
	});

	it("posts the operator's approve decision to the resolve endpoint and hides the controls", async () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "waiting", requiresApproval: true, pendingApprovalRequestId: "approval-42" })} />);

		fireEvent.click(screen.getByTestId("chat-tool-call-approve-get_time"));

		await waitFor(() => expect(resolveApprovalSpy).toHaveBeenCalledTimes(1));
		// TanStack passes (variables, context) to mutationFn — assert only the variables the card supplies.
		expect(resolveApprovalSpy.mock.calls[0]?.[0]).toEqual({ body: { requestId: "approval-42", approved: true, scope: "Once" } });
		// The decision is optimistic: the controls disappear as soon as the operator clicks.
		await waitFor(() => expect(screen.queryByTestId("chat-tool-call-approve-get_time")).toBeNull());
	});

	it("posts scope Session when the operator approves for the whole session", async () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "waiting", requiresApproval: true, pendingApprovalRequestId: "approval-42" })} />);

		fireEvent.click(screen.getByTestId("chat-tool-call-approve-session-get_time"));

		await waitFor(() => expect(resolveApprovalSpy).toHaveBeenCalledTimes(1));
		expect(resolveApprovalSpy.mock.calls[0]?.[0]).toEqual({ body: { requestId: "approval-42", approved: true, scope: "Session" } });
	});

	it("posts the operator's deny decision with approved=false and never a scope (a denial is not remembered)", async () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "waiting", requiresApproval: true, pendingApprovalRequestId: "approval-77" })} />);

		fireEvent.click(screen.getByTestId("chat-tool-call-deny-get_time"));

		await waitFor(() => expect(resolveApprovalSpy).toHaveBeenCalledTimes(1));
		expect(resolveApprovalSpy.mock.calls[0]?.[0]).toEqual({ body: { requestId: "approval-77", approved: false } });
	});

	it("re-arms the controls when the server prompts again with a new request id", async () => {
		// Session scope is withheld for imported skills and can be disabled by node policy, so the same tool can prompt
		// again right after a session approval. A per-card 'decided' boolean would leave that prompt unanswerable.
		const part = toolPart({ state: "waiting", requiresApproval: true, pendingApprovalRequestId: "approval-1" });
		const { rerender } = renderWithProviders(<ToolCallCard part={part} />);

		fireEvent.click(screen.getByTestId("chat-tool-call-approve-session-get_time"));
		await waitFor(() => expect(screen.queryByTestId("chat-tool-call-approve-get_time")).toBeNull());

		rerender(
			<QueryClientProvider client={new QueryClient({ defaultOptions: { mutations: { retry: false } } })}>
				<MantineProvider>
					<ToolCallCard part={{ ...part, pendingApprovalRequestId: "approval-2" }} />
				</MantineProvider>
			</QueryClientProvider>,
		);

		expect(screen.getByTestId("chat-tool-call-approve-get_time")).toBeTruthy();
	});

	it("renders the inline answer card instead of the generic waiting body when the part carries a question", () => {
		renderWithProviders(
			<ToolCallCard
				part={toolPart({
					name: "ask_user",
					state: "waiting",
					requiresApproval: true,
					// An ask_user call is approval-routed, so it also carries an approval request id; the question
					// payload must win, otherwise the operator gets Approve/Deny on a question they cannot answer.
					pendingApprovalRequestId: "approval-42",
					pendingQuestion: {
						requestId: "question-42",
						questions: [{ question: "Which auth method?", options: [{ label: "OAuth" }, { label: "API key" }] }],
					},
				})}
			/>,
		);

		expect(screen.getByTestId("chat-ask-user-card")).toBeTruthy();
		expect(screen.queryByTestId("chat-tool-call-approve-ask_user")).toBeNull();
	});

	it("starts minimized and keeps an operator-expanded state across a remount (keyed by tool id)", () => {
		// Default is collapsed.
		const first = renderWithProviders(<ToolCallCard part={toolPart({ id: "persist-1", name: "persist_tool" })} />);
		const details = screen.getByTestId("chat-tool-call-disclosure-persist_tool") as HTMLDetailsElement;
		expect(details.open).toBe(false);

		// Operator expands the card; the native <details> toggle drives the controlled state + the persistence map.
		details.open = true;
		fireEvent(details, new Event("toggle", { bubbles: false }));
		expect((screen.getByTestId("chat-tool-call-disclosure-persist_tool") as HTMLDetailsElement).open).toBe(true);

		// The transient-streaming → persisted-message swap unmounts and remounts the card; the expand survives.
		first.unmount();
		renderWithProviders(<ToolCallCard part={toolPart({ id: "persist-1", name: "persist_tool" })} />);
		expect((screen.getByTestId("chat-tool-call-disclosure-persist_tool") as HTMLDetailsElement).open).toBe(true);
	});
});
