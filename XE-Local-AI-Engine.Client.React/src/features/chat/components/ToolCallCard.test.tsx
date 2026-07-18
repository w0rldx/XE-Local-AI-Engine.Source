// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ToolCallCard } from "@/features/chat/components/ToolCallCard";
import type { ChatToolPart } from "@/features/chat/models/ChatModels";

// The Approve/Deny controls fire the generated resolve-approval mutation; stub its mutationFn so the click wiring can
// be asserted without a backend. `withResponseValidation` (which the card composes over the mutation) preserves the
// mutationFn, so the spy still receives the mutate variables. Only this one export is overridden.
const resolveApprovalSpy = vi.fn().mockResolvedValue({ requestId: "approval-42", approved: true });
vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	resolveToolApprovalMutation: () => ({ mutationFn: resolveApprovalSpy }),
}));

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

	it("renders Approve/Deny controls while awaiting an approval decision (UX-01)", () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "waiting", requiresApproval: true, pendingApprovalRequestId: "approval-42" })} />);

		expect(screen.getByTestId("chat-tool-call-approve-get_time")).toBeTruthy();
		expect(screen.getByTestId("chat-tool-call-deny-get_time")).toBeTruthy();
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

	it("posts the operator's approve decision to the resolve endpoint and hides the controls (UX-01)", async () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "waiting", requiresApproval: true, pendingApprovalRequestId: "approval-42" })} />);

		fireEvent.click(screen.getByTestId("chat-tool-call-approve-get_time"));

		await waitFor(() => expect(resolveApprovalSpy).toHaveBeenCalledTimes(1));
		// TanStack passes (variables, context) to mutationFn — assert only the variables the card supplies.
		expect(resolveApprovalSpy.mock.calls[0]?.[0]).toEqual({ body: { requestId: "approval-42", approved: true } });
		// The decision is optimistic: the controls disappear as soon as the operator clicks.
		await waitFor(() => expect(screen.queryByTestId("chat-tool-call-approve-get_time")).toBeNull());
	});

	it("posts the operator's deny decision with approved=false (UX-01)", async () => {
		renderWithProviders(<ToolCallCard part={toolPart({ state: "waiting", requiresApproval: true, pendingApprovalRequestId: "approval-77" })} />);

		fireEvent.click(screen.getByTestId("chat-tool-call-deny-get_time"));

		await waitFor(() => expect(resolveApprovalSpy).toHaveBeenCalledTimes(1));
		expect(resolveApprovalSpy.mock.calls[0]?.[0]).toEqual({ body: { requestId: "approval-77", approved: false } });
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
