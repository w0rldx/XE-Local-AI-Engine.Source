// Verifies that the Graph Workflows i18n keys stay in parity between en.json and every other locale. Parity is opt-in
// per feature, so this area owns its own file the way devWorkflows and work sessions do. Not jsdom-scoped: it operates
// purely on the JSON locale files.
//
// This lands with the FIRST keys rather than as polish. Ten closed vocabularies are looked up by their narrowed value,
// so a missing German string is a `MissingKey` render rather than a compile error — cheap to hold at 70 keys, miserable
// to retrofit at 300.

import { describe, expect, it } from "vitest";

import {
	graphWorkflowConditionOperators,
	graphWorkflowDecisionKinds,
	graphWorkflowEventTypeLabelKey,
	graphWorkflowEventTypes,
	graphWorkflowFailureClasses,
	graphWorkflowJoinPolicies,
	graphWorkflowNodeKinds,
	graphWorkflowNodeRunStatuses,
	graphWorkflowRunStatuses,
	graphWorkflowTabs,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { graphWorkflowGraphRules } from "@/features/graphWorkflows/models/GraphWorkflowValidation";
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
	{ name: "graphWorkflowsPages", enKeys: sectionKeys(en as LocaleShape, "pages", "graphWorkflows.") },
	{ name: "graphWorkflowsNavigation", enKeys: sectionKeys(en as LocaleShape, "navigation", "graphWorkflows") },
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
describe("graph-workflow enum label maps are complete", () => {
	const vocabularies = [
		{ section: "nodeKind", members: graphWorkflowNodeKinds },
		{ section: "nodeStatus", members: graphWorkflowNodeRunStatuses },
		{ section: "runStatus", members: graphWorkflowRunStatuses },
		{ section: "decision", members: graphWorkflowDecisionKinds },
		{ section: "failureClass", members: graphWorkflowFailureClasses },
		{ section: "joinPolicy", members: graphWorkflowJoinPolicies },
		{ section: "conditionOperator", members: graphWorkflowConditionOperators },
		{ section: "tab", members: graphWorkflowTabs },
		// The rule name IS the key suffix, so a rule added without a message fails here rather than rendering its own name
		// into the validation strip.
		{ section: "definition.issues", members: graphWorkflowGraphRules },
	] as const;

	it.each(vocabularies)("$section has a label for every member in en.json", ({ section, members }) => {
		for (const member of members) {
			expect(resolvePath(en as LocaleShape, `pages.graphWorkflows.${section}.${member}`), `${section}.${member}`).toBeTypeOf(
				"string",
			);
		}
	});

	// Event tokens are dotted (`run.created`), and i18next reads `.` as a key separator, so their labels are keyed with
	// underscores. `graphWorkflowEventTypeLabelKey` owns that mapping and this is the assertion that it resolves.
	it("eventType has a label for every one of the nineteen tokens", () => {
		for (const eventType of graphWorkflowEventTypes) {
			expect(resolvePath(en as LocaleShape, graphWorkflowEventTypeLabelKey(eventType)), eventType).toBeTypeOf("string");
		}
	});

	// A condition operator is shown to an operator building a branch; the raw token is exactly what must not leak.
	it("never shows a condition operator as its own wire token", () => {
		for (const operator of graphWorkflowConditionOperators) {
			const label = String(resolvePath(en as LocaleShape, `pages.graphWorkflows.conditionOperator.${operator}`));
			expect(label, operator).not.toBe(operator);
		}
	});
});
