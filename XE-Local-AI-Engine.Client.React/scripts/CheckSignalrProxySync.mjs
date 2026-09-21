import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(scriptDirectory, "..");
const repositoryRoot = resolve(frontendRoot, "..");

function blank(character) {
	return character === "\r" || character === "\n" ? character : " ";
}

export function maskCSharpTrivia(source) {
	const output = source.split("");
	let index = 0;
	while (index < source.length) {
		const next = source[index + 1];
		if (source[index] === "/" && next === "/") {
			while (index < source.length && source[index] !== "\n") {
				output[index] = blank(source[index]);
				index += 1;
			}
			continue;
		}
		if (source[index] === "/" && next === "*") {
			while (index < source.length) {
				const closesComment = source[index] === "*" && source[index + 1] === "/";
				output[index] = blank(source[index]);
				index += 1;
				if (closesComment) {
					output[index] = blank(source[index]);
					index += 1;
					break;
				}
			}
			continue;
		}
		if (source[index] === '"') {
			const verbatim = source[index - 1] === "@";
			output[index] = " ";
			index += 1;
			while (index < source.length) {
				const character = source[index];
				output[index] = blank(character);
				index += 1;
				if (character !== '"') {
					if (!verbatim && character === "\\" && index < source.length) {
						output[index] = blank(source[index]);
						index += 1;
					}
					continue;
				}
				if (verbatim && source[index] === '"') {
					output[index] = " ";
					index += 1;
					continue;
				}
				break;
			}
			continue;
		}
		if (source[index] === "'") {
			output[index] = " ";
			index += 1;
			while (index < source.length) {
				const character = source[index];
				output[index] = blank(character);
				index += 1;
				if (character === "\\" && index < source.length) {
					output[index] = blank(source[index]);
					index += 1;
				} else if (character === "'") {
					break;
				}
			}
			continue;
		}
		index += 1;
	}
	return output.join("");
}

function skipWhitespace(source, index) {
	let cursor = index;
	while (/\s/.test(source[cursor] ?? "")) {
		cursor += 1;
	}
	return cursor;
}

function findMatching(source, openingIndex, opening, closing, label) {
	let depth = 0;
	for (let cursor = openingIndex; cursor < source.length; cursor += 1) {
		if (source[cursor] === opening) {
			depth += 1;
		} else if (source[cursor] === closing) {
			depth -= 1;
			if (depth === 0) {
				return cursor;
			}
		}
	}
	throw new Error(`Unmatched ${opening} in active ${label}.`);
}

export function extractHubRouteConstants(source) {
	const routes = new Map();
	const structuralSource = maskCSharpTrivia(source);
	const classPattern = /public static class\s+(\w+)\s*\{/g;
	for (const classMatch of structuralSource.matchAll(classPattern)) {
		const className = classMatch[1];
		if (!className || className === "LocalApiRoutes" || classMatch.index === undefined) {
			continue;
		}
		const openingBrace = structuralSource.indexOf("{", classMatch.index);
		const closingBrace = findMatching(structuralSource, openingBrace, "{", "}", `LocalApiRoutes.${className}`);
		const body = source.slice(openingBrace + 1, closingBrace);
		const constantPattern = /public const string\s+(\w+)\s*=\s*"([^"]+)"/g;
		for (const constantMatch of body.matchAll(constantPattern)) {
			const [, constantName, route] = constantMatch;
			if (constantName && route) {
				routes.set(`LocalApiRoutes.${className}.${constantName}`, route);
			}
		}
	}
	return routes;
}

export function extractMappedHubPaths(programSource, routeSource) {
	const constants = extractHubRouteConstants(routeSource);
	const structuralSource = maskCSharpTrivia(programSource);
	const paths = [];
	for (const occurrence of structuralSource.matchAll(/\bMapHub\b/g)) {
		if (occurrence.index === undefined) {
			continue;
		}
		let cursor = skipWhitespace(structuralSource, occurrence.index + occurrence[0].length);
		if (structuralSource[cursor] !== "<") {
			throw new Error("Active MapHub occurrence is not a recognized generic invocation.");
		}
		cursor = skipWhitespace(structuralSource, findMatching(structuralSource, cursor, "<", ">", "MapHub generic") + 1);
		if (structuralSource[cursor] !== "(") {
			throw new Error("Active MapHub occurrence has no invocation argument list.");
		}
		const closingParenthesis = findMatching(structuralSource, cursor, "(", ")", "MapHub invocation");
		const argument = programSource.slice(cursor + 1, closingParenthesis).trim();
		const reference = argument.match(/^LocalApiRoutes\.\w+\.\w+$/)?.[0];
		if (reference) {
			const path = constants.get(reference);
			if (!path) {
				throw new Error(`Could not resolve mapped SignalR route ${reference}.`);
			}
			paths.push(path);
			continue;
		}
		const literal = argument.match(/^"([^"\r\n]+)"$/)?.[1];
		if (literal) {
			paths.push(literal);
			continue;
		}
		throw new Error(`Unrecognized active MapHub route argument: ${argument || "<empty>"}.`);
	}
	return paths;
}

export function compareProxyPaths(mappedPaths, proxyPaths) {
	const missing = mappedPaths.filter((path) => !proxyPaths.includes(path));
	const stale = proxyPaths.filter((path) => !mappedPaths.includes(path));
	return { missing, stale };
}

/**
 * Every hub payload enum the client keeps its OWN member list for. The generated OpenAPI unions are covered by
 * `openapi:check`; these are hand-written, so nothing but this check couples them to the C# enum. A member the server
 * can send and the client's list omits is not a type error anywhere — a zod `z.enum` silently rejects the whole frame
 * and the feature goes quiet, which is how `EvaluationState`/`EvaluationProgress` were dropped for the whole life of
 * the training evaluation stream.
 */
export const hubPayloadEnums = [
	{
		name: "TrainingRunEventKind",
		csharpPath: "XE-Local-AI-Engine.Client.Application/Services/Training/Runs/TrainingRunEvents.cs",
		clientPath: "src/features/training/models/TrainingRunModels.ts",
		anchor: "kind: z.enum([",
		terminator: "]",
	},
	{
		name: "DatasetGenerationEventKind",
		csharpPath: "XE-Local-AI-Engine.Client.Application/Services/Training/Datasets/DatasetGenerationEvents.cs",
		clientPath: "src/features/training/models/TrainingDatasetModels.ts",
		anchor: "kind: z.enum([",
		terminator: "]",
	},
	{
		name: "BenchmarkRunStreamEventKind",
		csharpPath: "XE-Local-AI-Engine.Client.Application/Services/Benchmarks/BenchmarkExecutionContracts.cs",
		clientPath: "src/features/benchmarks/models/BenchmarkLiveStreamModels.ts",
		anchor: "const benchmarkRunEventKinds = [",
		terminator: "]",
	},
	{
		name: "DevelopmentAttemptLiveUpdateKind",
		csharpPath: "XE-Local-AI-Engine.Client.Application/Services/Development/DevelopmentAttemptLiveUpdate.cs",
		clientPath: "src/features/development/models/DevelopmentModels.ts",
		anchor: "export type DevelopmentLiveUpdateKind =",
		terminator: ";",
	},
	{
		name: "ScheduledRunStatus",
		csharpPath: "XE-Local-AI-Engine.Client.Persistence/Entities/ScheduledRunStatus.cs",
		clientPath: "src/features/scheduler/models/SchedulerModels.ts",
		anchor: "export const scheduledRunStatuses: readonly ScheduledRunStatus[] = [",
		terminator: "]",
	},
];

export function extractCSharpEnumMembers(source, enumName) {
	const structuralSource = maskCSharpTrivia(source);
	const declaration = new RegExp(`\\benum\\s+${enumName}\\s*(?::\\s*\\w+\\s*)?\\{`).exec(structuralSource);
	if (!declaration) {
		throw new Error(`Could not find the C# enum ${enumName}.`);
	}
	const openingBrace = structuralSource.indexOf("{", declaration.index);
	const closingBrace = findMatching(structuralSource, openingBrace, "{", "}", `enum ${enumName}`);
	return structuralSource
		.slice(openingBrace + 1, closingBrace)
		.split(",")
		.map((member) => /^\s*([A-Za-z_]\w*)/.exec(member)?.[1])
		.filter((member) => member !== undefined);
}

export function extractQuotedNames(source, anchor, terminator) {
	const start = source.indexOf(anchor);
	if (start < 0) {
		throw new Error(`Could not find the client member list anchored at ${anchor}.`);
	}
	const from = start + anchor.length;
	const end = source.indexOf(terminator, from);
	if (end < 0) {
		throw new Error(`Client member list anchored at ${anchor} is not terminated by ${terminator}.`);
	}
	return [...source.slice(from, end).matchAll(/"([^"\r\n]+)"/g)].map((match) => match[1]);
}

export function compareEnumMembers(serverMembers, clientMembers) {
	return {
		missing: serverMembers.filter((member) => !clientMembers.includes(member)),
		stale: clientMembers.filter((member) => !serverMembers.includes(member)),
	};
}

export function checkHubPayloadEnums({ repositoryRoot: root = repositoryRoot, frontendRoot: frontend = frontendRoot } = {}) {
	const failures = [];
	for (const pair of hubPayloadEnums) {
		const serverMembers = extractCSharpEnumMembers(readFileSync(resolve(root, pair.csharpPath), "utf8"), pair.name);
		const clientMembers = extractQuotedNames(
			readFileSync(resolve(frontend, pair.clientPath), "utf8"),
			pair.anchor,
			pair.terminator,
		);
		const { missing, stale } = compareEnumMembers(serverMembers, clientMembers);
		if (missing.length > 0) {
			failures.push(`${pair.name}: the server can send ${missing.join(", ")} but ${pair.clientPath} does not list them.`);
		}
		if (stale.length > 0) {
			failures.push(`${pair.name}: ${pair.clientPath} lists ${stale.join(", ")}, which the C# enum no longer declares.`);
		}
	}
	if (failures.length > 0) {
		throw new Error(["Hub payload enums drifted from their client member lists.", ...failures].join("\n"));
	}
	return hubPayloadEnums.length;
}

export function checkSignalrProxySync({
	programPath = resolve(repositoryRoot, "XE-Local-AI-Engine.Client/Program.cs"),
	routesPath = resolve(repositoryRoot, "XE-Local-AI-Engine.Client/Endpoints/Common/LocalApiRoutes.cs"),
	proxyPathsPath = resolve(frontendRoot, "config/signalr-proxy-paths.json"),
} = {}) {
	const mappedPaths = extractMappedHubPaths(readFileSync(programPath, "utf8"), readFileSync(routesPath, "utf8"));
	const proxyPaths = JSON.parse(readFileSync(proxyPathsPath, "utf8"));
	const { missing, stale } = compareProxyPaths(mappedPaths, proxyPaths);
	if (missing.length > 0 || stale.length > 0) {
		throw new Error(
			[
				"Vite SignalR proxy paths are out of sync with Program.cs MapHub registrations.",
				...(missing.length > 0 ? [`Missing proxy paths: ${missing.join(", ")}`] : []),
				...(stale.length > 0 ? [`Stale proxy paths: ${stale.join(", ")}`] : []),
			].join("\n"),
		);
	}
	return mappedPaths.length;
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	try {
		const count = checkSignalrProxySync();
		const enumCount = checkHubPayloadEnums();
		process.stdout.write(
			`SignalR proxy paths match all ${count} Program.cs hub registrations; ${enumCount} hub payload enums match their client member lists.\n`,
		);
	} catch (error) {
		process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
		process.exitCode = 1;
	}
}
