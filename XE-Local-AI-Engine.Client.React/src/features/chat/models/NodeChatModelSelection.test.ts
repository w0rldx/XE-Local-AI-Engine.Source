import { describe, expect, it } from "vitest";

import { localDefaultModelValue, toNodeChatRequestModel } from "@/features/chat/models/NodeChatModelSelection";

describe("node chat model selection", () => {
	it("omits the local default sentinel so the backend can use the configured runtime model", () => {
		expect(toNodeChatRequestModel(localDefaultModelValue)).toBeUndefined();
	});

	it("passes explicit model selections through after trimming", () => {
		expect(toNodeChatRequestModel(" qwen3.5:0.8b ")).toBe("qwen3.5:0.8b");
	});
});
