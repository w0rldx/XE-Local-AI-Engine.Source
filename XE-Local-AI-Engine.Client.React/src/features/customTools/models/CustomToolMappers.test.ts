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
		allowUnattended: false,
		parameters: [],
		http: {
			method: "GET",
			urlTemplate: "https://api.example.com?city={city}",
			headers: [],
			bodyTemplate: "",
			allowedHosts: [],
			privateHosts: [],
		},
		command: {
			executable: "/usr/bin/curl",
			argsTemplate: ["--silent"],
			workingDirectory: "",
			timeoutSeconds: 0,
			env: [],
			detach: false,
		},
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
			baseForm({
				mode: "Parameterized",
				parameters: [{ name: "city", type: "string", description: "the city", required: true }],
			}),
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
					detach: false,
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
				allowUnattended: false,
				version: 2,
				createdAtUtc: 1,
				updatedAtUtc: 2,
				parameters: [],
				http: { method: "GET", urlTemplate: "https://api.example.com", headers: [], allowedHosts: [], privateHosts: [] },
			}),
		);
		expect(form.name).toBe("weather");
		expect(form.acknowledged).toBe(false);
	});
});

describe("allowUnattended and privateHosts", () => {
	it("reads both fields off the response and seeds the form with them", () => {
		const view = toCustomToolView({
			id: "t2",
			name: "custom__lights_on",
			description: "Turns the lights on",
			kind: "HttpFetch",
			mode: "Fixed",
			enabled: true,
			acknowledged: true,
			allowUnattended: true,
			version: 1,
			createdAtUtc: 1,
			updatedAtUtc: 1,
			parameters: [],
			http: {
				method: "POST",
				urlTemplate: "http://192.168.1.20:8123/api/services/light/turn_on",
				headers: [],
				allowedHosts: ["192.168.1.20"],
				privateHosts: ["192.168.1.20"],
			},
		});

		expect(view.allowUnattended).toBe(true);
		expect(view.http?.privateHosts).toEqual(["192.168.1.20"]);
		const form = toFormValues(view);
		expect(form.allowUnattended).toBe(true);
		expect(form.http.privateHosts).toEqual(["192.168.1.20"]);
	});

	it("defaults the private hosts when a stored tool predates them", () => {
		const view = toCustomToolView({
			id: "t3",
			name: "custom__weather",
			description: "Fetch weather",
			kind: "HttpFetch",
			mode: "Fixed",
			enabled: true,
			acknowledged: true,
			allowUnattended: false,
			version: 1,
			createdAtUtc: 1,
			updatedAtUtc: 1,
			parameters: [],
			http: { method: "GET", urlTemplate: "https://api.example.com" },
		});

		expect(view.http?.privateHosts).toEqual([]);
	});

	it("sends the flag and the trimmed, non-blank private hosts", () => {
		const definition = toDefinition(
			baseForm({
				allowUnattended: true,
				http: {
					method: "GET",
					urlTemplate: "http://192.168.1.20/api",
					headers: [],
					bodyTemplate: "",
					allowedHosts: ["192.168.1.20"],
					privateHosts: [" 192.168.1.20 ", "  "],
				},
			}),
		);

		expect(definition.allowUnattended).toBe(true);
		expect(definition.http?.privateHosts).toEqual(["192.168.1.20"]);
	});
});

describe("command detach", () => {
	function commandDto(command: { executable: string; detach?: boolean }) {
		return {
			id: "t4",
			name: "custom__open_word",
			description: "Opens Word",
			kind: "Command" as const,
			mode: "Fixed" as const,
			enabled: true,
			acknowledged: true,
			allowUnattended: true,
			version: 1,
			createdAtUtc: 1,
			updatedAtUtc: 1,
			parameters: [],
			command: { argsTemplate: [], timeoutSeconds: 0, env: [], ...command },
		};
	}

	it("reads the flag off the response, defaults it for a stored tool without it, and sends it back", () => {
		const detached = toCustomToolView(commandDto({ executable: "/usr/bin/word", detach: true }));
		expect(detached.command?.detach).toBe(true);
		expect(toDefinition(toFormValues(detached)).command?.detach).toBe(true);

		expect(toCustomToolView(commandDto({ executable: "/usr/bin/word" })).command?.detach).toBe(false);
	});
});
