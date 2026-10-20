// The `sceneSpec` answer format (schema v1), parsed for the run pane's viewer. The node's `BenchmarkSceneSpec` is the
// verifier's truth and owns the score; this copy only decides what the viewer draws, so a drift here can never change
// a verdict. Same rules, caps and defaults on purpose, so the viewer refuses what the verifier refuses.

import type { BenchmarkOutputPart } from "@/features/benchmarks/models/BenchmarkRunModels";

export const benchmarkScenePrimitives = ["box", "sphere", "cylinder", "cone", "plane"] as const;
export type BenchmarkScenePrimitive = (typeof benchmarkScenePrimitives)[number];

/** Mirrors the node's caps; also bound the `sceneSpec` config (`objectCount` 1..64, ≤ 16 relations). */
export const benchmarkSceneLimits = { maxObjects: 64, maxIdLength: 64, maxRelations: 16 } as const;

type Vector3 = [number, number, number];

export interface BenchmarkSceneObject {
	id: string;
	primitive: BenchmarkScenePrimitive;
	position: Vector3;
	/** Degrees. */
	rotation: Vector3;
	scale: Vector3;
	color: string;
}

export interface BenchmarkScene {
	objects: BenchmarkSceneObject[];
}

const sceneMembers = ["version", "objects"];
const objectMembers = ["id", "primitive", "position", "rotation", "scale", "color"];

/** The graded answer: output text in order, reasoning excluded. Parts are streamed chunks, so they join with nothing. */
export const answerText = (parts: readonly BenchmarkOutputPart[]): string =>
	parts
		.filter((part) => (part.kind === "output" || part.kind === "text") && part.content)
		.map((part) => part.content)
		.join("");

/** The first fenced block's body, else the whole trimmed text, as the node's `ExtractJson` reads it. */
export function extractFencedJson(text: string): string {
	const fence = /```[^\n]*\n([\s\S]*?)```/.exec(text);
	return (fence?.[1] ?? text).trim();
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
	typeof value === "object" && value !== null && !Array.isArray(value);

function readVector(value: unknown, isInRange: (n: number) => boolean): Vector3 | null {
	return Array.isArray(value) &&
		value.length === 3 &&
		value.every((n) => typeof n === "number" && Number.isFinite(n) && isInRange(n))
		? (value as Vector3)
		: null;
}

function parseObject(value: unknown, label: string, seen: Set<string>): BenchmarkSceneObject | string {
	if (!isRecord(value)) {
		return `${label}: not an object`;
	}
	const unknown = Object.keys(value).find((key) => !objectMembers.includes(key));
	if (unknown !== undefined) {
		return `${label}: unknown member '${unknown}'`;
	}
	const { id, primitive, position, rotation, scale, color } = value;
	if (typeof id !== "string" || id.length === 0 || id.length > benchmarkSceneLimits.maxIdLength) {
		return `${label}: id must be a non-empty string of at most ${benchmarkSceneLimits.maxIdLength} characters`;
	}
	if (seen.has(id)) {
		return `${label}: duplicate id '${id}'`;
	}
	seen.add(id);
	const kind = benchmarkScenePrimitives.find((option) => option === primitive);
	if (kind === undefined) {
		return `${label}: primitive must be one of ${benchmarkScenePrimitives.join(", ")}`;
	}
	const at = readVector(position, (n) => n >= -1000 && n <= 1000);
	if (at === null) {
		return `${label}: position must be three numbers in [-1000, 1000]`;
	}
	const turn = rotation === undefined ? [0, 0, 0] : readVector(rotation, (n) => n >= -360 && n <= 360);
	if (turn === null) {
		return `${label}: rotation must be three numbers in [-360, 360]`;
	}
	const size = scale === undefined ? [1, 1, 1] : readVector(scale, (n) => n > 0 && n <= 1000);
	if (size === null) {
		return `${label}: scale must be three numbers in (0, 1000]`;
	}
	if (color !== undefined && (typeof color !== "string" || !/^#[0-9a-fA-F]{6}$/.test(color))) {
		return `${label}: color must be #rrggbb`;
	}
	return {
		id,
		primitive: kind,
		position: at,
		rotation: turn as Vector3,
		scale: size as Vector3,
		color: (color as string | undefined) ?? "#808080",
	};
}

/** The answer's scene, or the first rule it breaks, worded like the verifier's detail. Never throws. */
export function parseSceneSpec(answer: string): { scene: BenchmarkScene } | { error: string } {
	let root: unknown;
	try {
		root = JSON.parse(extractFencedJson(answer));
	} catch {
		return { error: "the answer holds no valid JSON" };
	}
	if (!isRecord(root)) {
		return { error: "the scene must be a JSON object" };
	}
	const unknown = Object.keys(root).find((key) => !sceneMembers.includes(key));
	if (unknown !== undefined) {
		return { error: `unknown member '${unknown}'` };
	}
	if (root["version"] !== 1) {
		return { error: "version must be 1" };
	}
	const objects = root["objects"];
	if (!Array.isArray(objects) || objects.length === 0 || objects.length > benchmarkSceneLimits.maxObjects) {
		return { error: `objects must be an array of 1 to ${benchmarkSceneLimits.maxObjects} objects` };
	}
	const seen = new Set<string>();
	const parsed: BenchmarkSceneObject[] = [];
	for (const [index, value] of objects.entries()) {
		const object = parseObject(value, `object ${index + 1}`, seen);
		if (typeof object === "string") {
			return { error: object };
		}
		parsed.push(object);
	}
	return { scene: { objects: parsed } };
}

/** Vertical field of view, in degrees, of the viewer's camera. */
export const sceneCameraFov = 50;

/**
 * Camera distance at which a bounding sphere of `radius` fits the view at `aspect` (width / height). The narrower of
 * the vertical and horizontal fields of view decides: in a pane narrower than it is tall the horizontal one is the
 * tighter, and fitting the vertical one alone clipped the sides.
 */
export function sceneCameraDistance(radius: number, aspect: number): number {
	const vertical = (sceneCameraFov / 2) * (Math.PI / 180);
	const half = Math.min(vertical, Math.atan(Math.tan(vertical) * aspect));
	return (radius / Math.sin(half)) * 1.1;
}
