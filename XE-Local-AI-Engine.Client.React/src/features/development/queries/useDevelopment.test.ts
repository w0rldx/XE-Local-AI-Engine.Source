import { describe, expect, it } from "vitest";

import type { DevelopmentProjectDetail } from "@/features/development/models/DevelopmentModels";
import { developmentProjectPollInterval } from "@/features/development/queries/useDevelopment";

function detailWithAttempts(...statuses: string[]): DevelopmentProjectDetail {
	return { tasks: [{ attempts: statuses.map((status) => ({ status })) }] } as DevelopmentProjectDetail;
}

describe("developmentProjectPollInterval", () => {
	it("polls at 3 s while any task has an active attempt", () => {
		expect(developmentProjectPollInterval({ state: { data: detailWithAttempts("Succeeded", "Running") } })).toBe(3000);
		expect(developmentProjectPollInterval({ state: { data: detailWithAttempts("Pending") } })).toBe(3000);
	});

	it("polls at 3 s while a task is in Validation, which runs without an active attempt", () => {
		const data = { tasks: [{ task: { status: "Validation" }, attempts: [{ status: "Succeeded" }] }] } as DevelopmentProjectDetail;
		expect(developmentProjectPollInterval({ state: { data } })).toBe(3000);
	});

	it("falls back to the 5 s page-scoped cadence when nothing runs", () => {
		expect(developmentProjectPollInterval({ state: { data: detailWithAttempts("Succeeded", "Failed") } })).toBe(5000);
		expect(developmentProjectPollInterval({ state: {} })).toBe(5000);
	});
});
