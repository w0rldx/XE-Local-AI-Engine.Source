import { describe, expect, it } from "vitest";

import {
	answerText,
	extractFencedJson,
	parseSceneSpec,
	sceneCameraDistance,
	sceneCameraFov,
} from "@/features/benchmarks/models/BenchmarkSceneSpec";

const object = (overrides: Record<string, unknown> = {}) => ({ id: "a", primitive: "box", position: [0, 0, 0], ...overrides });
const scene = (...objects: unknown[]) => JSON.stringify({ version: 1, objects });
const errorOf = (answer: string): string => {
	const result = parseSceneSpec(answer);
	return "error" in result ? result.error : "parsed";
};

describe("answerText", () => {
	// Parts are streamed chunks: a separator would split a number in the middle of the JSON.
	it("joins output chunks in order and leaves reasoning out", () => {
		expect(
			answerText([
				{ kind: "reasoning", content: "think" },
				{ kind: "output", content: '{"ver' },
				{ kind: "tool_call", toolName: "x" },
				{ kind: "output", content: 'sion":1}' },
			]),
		).toBe('{"version":1}');
	});
});

describe("extractFencedJson", () => {
	it("takes the first fence body", () => {
		expect(extractFencedJson('Here:\n```json\n{"a":1}\n```\nand ```\n{"b":2}\n```')).toBe('{"a":1}');
	});

	it("falls back to the whole trimmed text", () => {
		expect(extractFencedJson('  {"a":1}\n')).toBe('{"a":1}');
	});
});

describe("parseSceneSpec", () => {
	it("fills the optional members with their defaults", () => {
		expect(parseSceneSpec(`\`\`\`json\n${scene(object())}\n\`\`\``)).toEqual({
			scene: {
				objects: [{ id: "a", primitive: "box", position: [0, 0, 0], rotation: [0, 0, 0], scale: [1, 1, 1], color: "#808080" }],
			},
		});
	});

	it("keeps explicit members", () => {
		const result = parseSceneSpec(scene(object({ rotation: [0, 90, 0], scale: [2, 1, 3], color: "#FFAA00" })));
		expect("scene" in result && result.scene.objects[0]).toMatchObject({
			rotation: [0, 90, 0],
			scale: [2, 1, 3],
			color: "#FFAA00",
		});
	});

	it.each([
		["not json", "the answer holds no valid JSON"],
		["[]", "the scene must be a JSON object"],
		[JSON.stringify({ version: 1, objects: [object()], lights: [] }), "unknown member 'lights'"],
		[JSON.stringify({ version: 2, objects: [object()] }), "version must be 1"],
		[JSON.stringify({ version: 1, objects: [] }), "objects must be an array of 1 to 64 objects"],
		[
			scene(...Array.from({ length: 65 }, (_, index) => object({ id: `o${index}` }))),
			"objects must be an array of 1 to 64 objects",
		],
		[scene(object({ texture: "x.png" })), "object 1: unknown member 'texture'"],
		[scene(object({ id: "" })), "object 1: id must be a non-empty string of at most 64 characters"],
		[scene(object({ id: "x".repeat(65) })), "object 1: id must be a non-empty string of at most 64 characters"],
		[scene(object(), object()), "object 2: duplicate id 'a'"],
		[scene(object({ primitive: "Box" })), "object 1: primitive must be one of box, sphere, cylinder, cone, plane"],
		[scene({ id: "a", primitive: "box" }), "object 1: position must be three numbers in [-1000, 1000]"],
		[scene(object({ position: [0, 1001, 0] })), "object 1: position must be three numbers in [-1000, 1000]"],
		[scene(object({ rotation: [0, 0, 361] })), "object 1: rotation must be three numbers in [-360, 360]"],
		[scene(object({ scale: [0, 1, 1] })), "object 1: scale must be three numbers in (0, 1000]"],
		[scene(object({ color: "red" })), "object 1: color must be #rrggbb"],
	])("refuses %s", (answer, error) => {
		expect(errorOf(answer)).toBe(error);
	});
});

describe("sceneCameraDistance", () => {
	const halfVertical = (sceneCameraFov / 2) * (Math.PI / 180);

	it("fits the vertical field of view when the pane is at least as wide as it is tall", () => {
		expect(sceneCameraDistance(5, 1)).toBeCloseTo((5 / Math.sin(halfVertical)) * 1.1);
		expect(sceneCameraDistance(5, 2)).toBeCloseTo(sceneCameraDistance(5, 1));
	});

	it("backs off so the sphere fits the narrower horizontal field of view in a narrow pane", () => {
		const aspect = 240 / 320;
		const distance = sceneCameraDistance(5, aspect);
		const halfHorizontal = Math.atan(Math.tan(halfVertical) * aspect);
		expect(distance).toBeGreaterThan(sceneCameraDistance(5, 1));
		// The sphere's angular radius from the camera stays inside the horizontal half-angle.
		expect(Math.asin(5 / distance)).toBeLessThan(halfHorizontal);
	});
});
