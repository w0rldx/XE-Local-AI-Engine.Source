// Verifies that the Development Workflows i18n keys stay in parity between en.json and every other locale. Parity is
// opt-in per feature, so this area owns its own file the way work sessions do. Not jsdom-scoped: it operates purely on
// the JSON locale files.
//
// This lands with the FIRST keys rather than as a polish item (C38). Six closed vocabularies — 9 node statuses, 9 run
// statuses, 7 node types, 6 decision kinds, 10 artifact kinds, 5 work-item statuses, 10 failure classes and 3 queue
// reasons — are looked up by their narrowed value, so a missing German string is a `MissingKey` render rather than a
// compile error. Cheap to hold at 50 keys, miserable to retrofit at 300.

import { describe, expect, it } from "vitest";

import { devWorkflowGraphRules } from "@/features/devWorkflows/models/DevWorkflowDefinitionValidation";
import en from "@/locales/en.json";
import { nonEnglishLocales } from "@/test/Locales";

type LocaleShape = Record<string, unknown>;

function collectKeys(obj: LocaleShape, prefix = ""): string[] {
	const result: string[] = [];
	for (const [key, value] of Object.entries(obj)) {
		const path = prefix ? `${prefix}.${key}` : key;
		if (value !== null && typeof value === "object" && !Array.isArray(value)) {
			result.push(...collectKeys(value as LocaleShape, path));
		} else {
			result.push(path);
		}
	}
	return result;
}

function resolvePath(obj: LocaleShape, path: string): unknown {
	return path.split(".").reduce<unknown>((acc, segment) => {
		if (acc === undefined || acc === null || typeof acc !== "object") {
			return undefined;
		}
		return (acc as LocaleShape)[segment];
	}, obj);
}

function sectionKeys(resource: LocaleShape, rootSection: string, subPrefix = ""): string[] {
	const root = resource[rootSection] as LocaleShape | undefined;
	if (!root) {
		return [];
	}
	return collectKeys(root)
		.filter((key) => key.startsWith(subPrefix))
		.map((key) => `${rootSection}.${key}`);
}

const sections = [
	{ name: "devWorkflowsPages", enKeys: sectionKeys(en as LocaleShape, "pages", "devWorkflows.") },
	{ name: "devWorkflowsNavigation", enKeys: sectionKeys(en as LocaleShape, "navigation", "devWorkflows") },
] as const;

describe.each(sections)("$name i18n key parity (en ↔ every locale)", ({ name, enKeys }) => {
	it(`has at least one ${name} key in en.json`, () => {
		expect(enKeys.length).toBeGreaterThan(0);
	});

	it.each(nonEnglishLocales)(`every en.json ${name} key exists in $code`, ({ resource }) => {
		const missing = enKeys.filter((key) => resolvePath(resource as LocaleShape, key) === undefined);
		expect(missing, `Missing in locale: ${missing.join(", ")}`).toHaveLength(0);
	});
});

// The enum label maps are the half that fails silently: an un-narrowed or newly added member renders the key itself.
// Asserting the maps whole here is what makes "the label map is complete" a test rather than a habit.
describe("dev-workflow enum label maps are complete in every locale", () => {
	const vocabularies = [
		{
			section: "nodeStatus",
			members: ["Pending", "Queued", "Running", "WaitingForApproval", "Blocked", "Succeeded", "Failed", "Skipped", "Cancelled"],
		},
		{
			section: "runStatus",
			members: ["Pending", "Running", "Pausing", "Paused", "WaitingForApproval", "Cancelling", "Completed", "Failed", "Cancelled"],
		},
		{ section: "nodeType", members: ["Agent", "Tool", "DevTask", "HumanGate", "Gate", "Parallel", "Join"] },
		{ section: "decision", members: ["Approve", "Reject", "RequestChanges", "Retry", "Skip", "Abandon"] },
		{
			section: "artifactKind",
			members: [
				"Research",
				"Decision",
				"Specification",
				"Plan",
				"TaskPackage",
				"Patch",
				"Report",
				"Finding",
				"ValidationReport",
				"ReviewReport",
			],
		},
		{ section: "workItemStatus", members: ["Draft", "Active", "Blocked", "Completed", "Cancelled"] },
		// C4's apply outcomes. Not narrowed client-side either — the panel falls back to the raw token — so this map is
		// the only thing standing between a `blocked` patch and a row that reads as a key.
		{ section: "applyOutcome", members: ["applied", "already-applied", "blocked", "refused", "cancelled"] },
		// Not narrowed client-side, so both carry a generic `unknown` fallback for a token a newer server invents.
		{ section: "queueReason", members: ["awaiting-agent-slot", "awaiting-sandbox-slot", "awaiting-dependency", "unknown"] },
		{
			section: "failureClass",
			members: [
				"ProviderError",
				"Timeout",
				"Interrupted",
				"ToolCommandFailed",
				"Internal",
				"Configuration",
				"Policy",
				"BudgetExhausted",
				"Cancelled",
				"GateRejected",
				"ObjectiveNotMet",
				"unknown",
			],
		},
		// C1's cross-unit failure vocabulary, projected from the ten DevWorkflow failure classes plus the two tokens only
		// the other two arms can write. `AgentUnitFailureClassTests.TheVocabulary_IsTwelveDistinctTokens` pins the C# side
		// at twelve; this is the other side of that claim — a token added there without a label here would reach an
		// operator as a raw identifier, and no C# test can see a locale file.
		{
			section: "node.failureGroup",
			members: [
				"Cancelled",
				"Timeout",
				"Interrupted",
				"Provider",
				"ModelCapability",
				"ContextExceeded",
				"Configuration",
				"Policy",
				"BudgetExhausted",
				"ToolOrCommand",
				"Rejected",
				"Internal",
			],
		},
		// Every rule the definition editor's save gate can report. The rule NAME is the key suffix, so a rule that ships
		// without a message renders as its own identifier in front of an operator trying to fix a graph.
		{ section: "definition.issues", members: devWorkflowGraphRules },
	] as const;

	it.each(vocabularies)("$section has a label for every member in en.json", ({ section, members }) => {
		for (const member of members) {
			expect(resolvePath(en as LocaleShape, `pages.devWorkflows.${section}.${member}`), `${section}.${member}`).toBeTypeOf("string");
		}
	});

	it("says nothing that reads literally as 'waiting for approval' at run level", () => {
		// The run-level WaitingForApproval covers an open gate AND a node needing intervention (Y20), so the badge must
		// not promise an approval when the truth may be "a node died and needs you".
		const label = resolvePath(en as LocaleShape, "pages.devWorkflows.runStatus.WaitingForApproval");
		expect(String(label).toLowerCase()).not.toContain("approval");
	});
});
