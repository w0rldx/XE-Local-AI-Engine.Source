// @vitest-environment jsdom

import { afterEach, describe, expect, it } from "vitest";

import { filterNewAccessibilityIssues, findAccessibilityIssues } from "./DevelopmentAccessibilityAudit";

describe("development accessibility audit", () => {
	afterEach(() => {
		document.body.innerHTML = "";
	});

	it("reports unlabeled interactive controls without flagging labeled controls", () => {
		document.body.innerHTML = `
			<button id="missing-name"><svg aria-hidden="true"></svg></button>
			<button aria-label="Open settings"><svg aria-hidden="true"></svg></button>
			<label for="search">Search</label><input id="search" />
			<input id="missing-label" />
		`;

		const issues = findAccessibilityIssues(document);

		expect(issues.map((issue) => issue.element.id)).toEqual(["missing-name", "missing-label"]);
	});

	it("deduplicates unchanged findings while allowing resolved findings to disappear", () => {
		document.body.innerHTML = '<button id="missing"></button>';
		const first = filterNewAccessibilityIssues(findAccessibilityIssues(document), new Map());
		const second = filterNewAccessibilityIssues(findAccessibilityIssues(document), first.current);
		document.querySelector("button")?.setAttribute("aria-label", "Named");
		const resolved = filterNewAccessibilityIssues(findAccessibilityIssues(document), second.current);

		expect(first.additions).toHaveLength(1);
		expect(second.additions).toHaveLength(0);
		expect(resolved.additions).toHaveLength(0);
		expect(resolved.current.size).toBe(0);
	});
});
