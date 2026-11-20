// @vitest-environment jsdom

import { afterEach, describe, expect, it } from "vitest";

import { alertedWaits, clearAlertedWaits, isOnOperationPage, recordAlertedWaits } from "@/core/operations/models/JobAlerts";
import type { Operation } from "@/core/operations/models/OperationModels";

afterEach(() => globalThis.localStorage.clear());

const session: Operation = {
	id: "work-session:s-1",
	kind: "work-session",
	label: "Work session Docs waits for your input",
	progress: { kind: "indeterminate" },
	state: "waiting",
	href: { to: "/work-sessions/$sessionId", params: { sessionId: "s-1" } },
};

const run: Operation = {
	id: "graph-run:run-9",
	kind: "graph-run",
	label: "A workflow waits for your decision",
	progress: { kind: "indeterminate" },
	state: "waiting",
	href: { to: "/graph-workflows", search: { definitionId: "def-1", runId: "run-9" } },
};

const at = (pathname: string, search = "") => ({ pathname, search });

describe("isOnOperationPage", () => {
	it("matches a parameterized link against the filled-in path only", () => {
		expect(isOnOperationPage(session, at("/work-sessions/s-1/"))).toBe(true);
		expect(isOnOperationPage(session, at("/work-sessions/s-2"))).toBe(false);
		expect(isOnOperationPage(session, at("/work-sessions"))).toBe(false);
	});

	it("takes only the run's own view as a graph run's page, not the editor or another run", () => {
		expect(isOnOperationPage(run, at("/graph-workflows", "?definitionId=def-1&runId=run-9"))).toBe(true);
		expect(isOnOperationPage(run, at("/graph-workflows", "?runId=run-8"))).toBe(false);
		expect(isOnOperationPage(run, at("/graph-workflows"))).toBe(false);
	});
});

describe("alerted waits", () => {
	it("adds claimed ids, forgets released ones, and keeps every id still waiting beyond the cap", () => {
		const waiting = Array.from({ length: 60 }, (_, index) => `graph-run:${index}`);
		recordAlertedWaits([], waiting, [], new Set(waiting));
		expect(alertedWaits()).toEqual(waiting);

		recordAlertedWaits(alertedWaits(), [], ["graph-run:0"], new Set(waiting.slice(1)));
		expect(alertedWaits()).toEqual(waiting.slice(1));
	});

	it("caps only the ids that are no longer waiting, oldest first", () => {
		const ended = Array.from({ length: 51 }, (_, index) => `graph-run:${index}`);
		recordAlertedWaits(ended, ["graph-run:new"], [], new Set(["graph-run:new"]));

		expect(alertedWaits()).toEqual([...ended.slice(1), "graph-run:new"]);
	});

	it("reads unreadable storage as none, and forgets everything on clear", () => {
		globalThis.localStorage.setItem("xe-alerted-waits", "{not json");
		expect(alertedWaits()).toEqual([]);

		recordAlertedWaits([], ["graph-run:a"], [], new Set(["graph-run:a"]));
		clearAlertedWaits();
		expect(alertedWaits()).toEqual([]);
	});
});
