// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { useChatWorkflowStore } from "@/features/chat/workflow/ChatWorkflowStore";

const initial = useChatWorkflowStore.getState();

describe("ChatWorkflowStore", () => {
	beforeEach(() => {
		localStorage.clear();
		useChatWorkflowStore.setState(initial, true);
	});

	afterEach(() => {
		localStorage.clear();
	});

	it("keeps picks per conversation and persists only the explicit 'No workflow' opt-outs", async () => {
		const { actions } = useChatWorkflowStore.getState();
		actions.selectDefinition("conversation-a", "definition-1");
		actions.selectDefinition("conversation-b", "");

		expect(useChatWorkflowStore.getState().selectedDefinitionByConversation).toEqual({
			"conversation-a": "definition-1",
			"conversation-b": "",
		});
		expect(JSON.parse(localStorage.getItem("xe-node-chat-workflow-opted-out") ?? "{}")).toEqual({ "conversation-b": "" });

		vi.resetModules();
		const reloaded = await import("@/features/chat/workflow/ChatWorkflowStore");
		expect(reloaded.useChatWorkflowStore.getState().selectedDefinitionByConversation).toEqual({ "conversation-b": "" });
	});

	it("carries the pick made before any conversation existed onto the new one, and drops the empty key", () => {
		const { actions } = useChatWorkflowStore.getState();
		actions.selectDefinition("", "definition-1");

		actions.carryOverPendingPick("conversation-new");

		expect(useChatWorkflowStore.getState().selectedDefinitionByConversation).toEqual({ "conversation-new": "definition-1" });
		// The pre-conversation opt-out is never persisted under the empty key.
		actions.selectDefinition("", "");
		expect(JSON.parse(localStorage.getItem("xe-node-chat-workflow-opted-out") ?? "{}")).toEqual({});
	});

	it("persists the activity toggle and restores it on the next load", async () => {
		useChatWorkflowStore.getState().actions.setShowActivity(false);

		expect(localStorage.getItem("xe-node-chat-workflow-show-activity")).toBe("false");
		vi.resetModules();
		const reloaded = await import("@/features/chat/workflow/ChatWorkflowStore");
		expect(reloaded.useChatWorkflowStore.getState().showActivity).toBe(false);
	});

	it("persists a dismissed run per conversation and ignores a corrupt stored value", async () => {
		useChatWorkflowStore.getState().actions.dismissRun("conversation-a", "run-1");

		expect(JSON.parse(localStorage.getItem("xe-node-chat-workflow-dismissed-runs") ?? "{}")).toEqual({
			"conversation-a": "run-1",
		});

		localStorage.setItem("xe-node-chat-workflow-dismissed-runs", "not json");
		vi.resetModules();
		const reloaded = await import("@/features/chat/workflow/ChatWorkflowStore");
		expect(reloaded.useChatWorkflowStore.getState().dismissedRunByConversation).toEqual({});
		expect(reloaded.useChatWorkflowStore.getState().showActivity).toBe(true);
	});

	it("still works when storage throws", () => {
		vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
			throw new Error("quota");
		});

		useChatWorkflowStore.getState().actions.setShowActivity(false);

		expect(useChatWorkflowStore.getState().showActivity).toBe(false);
	});
});
