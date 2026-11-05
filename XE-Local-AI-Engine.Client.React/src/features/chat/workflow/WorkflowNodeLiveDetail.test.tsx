// @vitest-environment jsdom

import { screen, within } from "@testing-library/react";
import i18next from "i18next";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";

import { WorkflowNodeLiveDetail } from "@/features/chat/workflow/WorkflowNodeLiveDetail";
import { WorkflowRunStatusCard } from "@/features/graphWorkflows/components/WorkflowRunStatusCard";
import { toWorkflowPath } from "@/features/graphWorkflows/models/ChatWorkflowModels";
import { eightNodeGraph, makeNodeRun } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The card links to the run view; the router itself is not this file's subject.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	Link: ({ children, to, ...props }: { children: ReactNode; to: string }) => (
		<a href={to} {...props}>
			{children}
		</a>
	),
}));

describe("WorkflowRunStatusCard", () => {
	it("shows the active node's live reasoning collapsed behind the chat's Thoughts disclosure", () => {
		const stream = { conversationId: "c", messageId: "m", content: "", reasoning: "Weighing three options", isActive: true };
		renderWithProviders(
			<WorkflowRunStatusCard
				runId="run-1"
				definitionId="def-1"
				workflowName="Support triage"
				status="Running"
				path={toWorkflowPath(eightNodeGraph, [makeNodeRun({ nodeKey: "lookup", status: "Running", completedAtUtc: null })])}
				stopping={false}
				onStop={vi.fn()}
				onDismiss={vi.fn()}
				liveDetail={<WorkflowNodeLiveDetail nodeKey="lookup" stream={stream} />}
			/>,
		);

		const card = screen.getByTestId("chat-workflow-status-card");
		const summary = within(card).getByTestId("chat-message-reasoning-summary-workflow-node-lookup");
		expect(summary.textContent).toContain(`${i18next.t("chat.thoughts")} · 3 ${i18next.t("chat.words", { count: 3 })}`);
		expect(within(card).getByTestId("chat-message-reasoning-workflow-node-lookup").hasAttribute("open")).toBe(false);
	});

	it("shows only what the frames said while no reasoning has arrived, and nothing when they said nothing", () => {
		const base = { conversationId: "c", messageId: "m", content: "", isActive: true };
		const { unmount } = renderWithProviders(
			<WorkflowNodeLiveDetail nodeKey="lookup" stream={{ ...base, runtimePhase: "loading_model", outputTokens: 12 }} />,
		);
		expect(screen.getByTestId("chat-workflow-live-lookup").textContent).toBe(
			`${i18next.t("pages.chat.loadingModel")} · ${i18next.t("pages.chat.workflow.live.tokens", { count: 12 })}`,
		);
		unmount();

		const { container } = renderWithProviders(<WorkflowNodeLiveDetail nodeKey="lookup" stream={base} />);
		expect(screen.queryByTestId("chat-workflow-live-lookup")).toBeNull();
		expect(container.textContent).not.toContain(i18next.t("chat.toolCall.thinkingLive"));
	});

	it("keeps saying Generating once content streams, because the fold clears the phase on every content delta", () => {
		const base = { conversationId: "c", messageId: "m", isActive: true };
		const { unmount } = renderWithProviders(
			<WorkflowNodeLiveDetail nodeKey="lookup" stream={{ ...base, content: "", runtimePhase: "generating" }} />,
		);
		expect(screen.getByTestId("chat-workflow-live-lookup").textContent).toBe(i18next.t("pages.chat.workflow.live.generating"));
		unmount();

		// The next content delta arrives with the phase cleared: the streamed text is the evidence of generation.
		renderWithProviders(<WorkflowNodeLiveDetail nodeKey="lookup" stream={{ ...base, content: "The invoice" }} />);
		expect(screen.getByTestId("chat-workflow-live-lookup").textContent).toBe(i18next.t("pages.chat.workflow.live.generating"));
	});
});
