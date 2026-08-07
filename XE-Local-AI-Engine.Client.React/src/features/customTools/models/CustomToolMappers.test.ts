import { describe, expect, it } from "vitest";

import { toCustomToolView, toDefinition, toFormValues } from "@/features/customTools/models/CustomToolMappers";
import { CUSTOM_TOOL_SECRET_SENTINEL, type CustomToolFormValues } from "@/features/customTools/models/CustomToolModels";

function baseForm(overrides: Partial<CustomToolFormValues> = {}): CustomToolFormValues {
	return {
		name: "weather",
		description: "Fetch weather",
		kind: "HttpFetch",
		mode: "Fixed",
		enabled: false,
		acknowledged: true,
		parameters: [],
		http: { method: "GET", urlTemplate: "https://api.example.com?city={city}", headers: [], bodyTemplate: "", allowedHosts: [] },
		command: { executable: "/usr/bin/curl", argsTemplate: ["--silent"], workingDirectory: "", timeoutSeconds: 0, env: [] },
		...overrides,
	};
}

describe("toDefinition", () => {
	it("sends only the active kind's block and null for the other", () => {
		const definition = toDefinition(baseForm({ kind: "HttpFetch" }));
		expect(definition.http).not.toBeNull();
		expect(definition.command).toBeNull();
	});

	it("drops parameters on a Fixed tool even when the form still carries stale rows", () => {
		const definition = toDefinition(
			baseForm({ mode: "Fixed", parameters: [{ name: "city", type: "string", description: "", required: true }] }),
		);
		expect(definition.parameters).toEqual([]);
	});

	it("keeps declared parameters on a Parameterized tool", () => {
		const definition = toDefinition(
			baseForm({ mode: "Parameterized", parameters: [{ name: "city", type: "string", description: "the city", required: true }] }),
		);
		expect(definition.parameters).toEqual([{ name: "city", type: "string", description: "the city", required: true }]);
	});

	it("passes a round-tripped secret sentinel through untouched so the stored secret is kept", () => {
		const definition = toDefinition(
			baseForm({
				kind: "Command",
				command: {
					executable: "/usr/bin/curl",
					argsTemplate: [],
					workingDirectory: "",
					timeoutSeconds: 0,
					env: [{ name: "TOKEN", value: CUSTOM_TOOL_SECRET_SENTINEL, isSecret: true }],
				},
			}),
		);
		expect(definition.command?.env?.[0]?.value).toBe(CUSTOM_TOOL_SECRET_SENTINEL);
	});
});

describe("toFormValues", () => {
	it("strips the custom__ prefix and forces a fresh acknowledgement", () => {
		const form = toFormValues(
			toCustomToolView({
				id: "t1",
				name: "custom__weather",
				description: "Fetch weather",
				kind: "HttpFetch",
				mode: "Fixed",
				enabled: true,
				acknowledged: true,
				version: 2,
				createdAtUtc: 1,
				updatedAtUtc: 2,
				parameters: [],
				http: { method: "GET", urlTemplate: "https://api.example.com", headers: [], allowedHosts: [] },
			}),
		);
		expect(form.name).toBe("weather");
		expect(form.acknowledged).toBe(false);
	});
});
