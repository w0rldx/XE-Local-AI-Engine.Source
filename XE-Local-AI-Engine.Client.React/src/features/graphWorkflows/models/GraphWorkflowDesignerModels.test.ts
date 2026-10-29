// The designer's pure helpers: what the review bar counts and what it names about the graph-level settings.

import { describe, expect, it } from "vitest";

import { designerSettingsDiff, summarizeDesignerChanges } from "@/features/graphWorkflows/models/GraphWorkflowDesignerModels";
import { designerChanges } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";

describe("summarizeDesignerChanges", () => {
	it("counts nodes and edges and leaves the graph-level settings entry to the settings diff", () => {
		expect(summarizeDesignerChanges([...designerChanges, { kind: "graph", key: "settings", change: "modified" }])).toEqual({
			added: 3,
			modified: 1,
			removed: 3,
		});
	});
});

describe("designerSettingsDiff", () => {
	it("names only the members that differ, at their effective values", () => {
		expect(
			designerSettingsDiff(
				{ kind: "Standard" },
				{ kind: "Chat", chat: { acceptsAttachments: false, requireRerunConfirmation: false } },
			),
		).toEqual([
			{ member: "kind", from: "Standard", to: "Chat" },
			// `acceptsAttachments: false` is the default an absent member already had, so it is no change.
			{ member: "requireRerunConfirmation", from: true, to: false },
		]);
	});

	it("is empty when nothing graph-level changes, and ignores the chat block of two Standard graphs", () => {
		expect(designerSettingsDiff({ kind: "Chat" }, { kind: "Chat", chat: { requireRerunConfirmation: true } })).toEqual([]);
		expect(designerSettingsDiff({ kind: "Standard" }, { kind: "Standard", chat: { acceptsAttachments: true } })).toEqual([]);
	});
});
