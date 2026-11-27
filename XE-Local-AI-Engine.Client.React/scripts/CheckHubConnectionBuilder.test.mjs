import { findHubConnectionBuilderUses } from "./CheckHubConnectionBuilder.mjs";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

function writeSources(context, files) {
	const root = mkdtempSync(join(tmpdir(), "xe-hub-builder-"));
	context.after(() => rmSync(root, { recursive: true, force: true }));
	for (const [path, content] of Object.entries(files)) {
		mkdirSync(join(root, path, ".."), { recursive: true });
		writeFileSync(join(root, path), content);
	}
	return root;
}

test("flags the namespace and default imports Biome lets through, and spares the builder and test files", (context) => {
	const root = writeSources(context, {
		"src/core/api/signalr/BuildHubConnection.ts": 'import { HubConnectionBuilder } from "@microsoft/signalr";\n',
		"src/features/a/Namespace.ts": 'import * as signalR from "@microsoft/signalr";\nnew signalR.HubConnectionBuilder();\n',
		"src/features/b/Default.tsx": 'import signalR from "@microsoft/signalr";\nnew signalR.HubConnectionBuilder();\n',
		"src/features/c/Uses.test.tsx": 'vi.mock("@microsoft/signalr", () => ({ HubConnectionBuilder: vi.fn() }));\n',
		"src/features/d/Clean.ts": 'import { buildHubConnection } from "@/core/api/signalr/BuildHubConnection";\n',
	});

	assert.deepEqual(findHubConnectionBuilderUses(root), ["src/features/a/Namespace.ts", "src/features/b/Default.tsx"]);
});

test("the real source tree names HubConnectionBuilder only in the builder", () => {
	assert.deepEqual(findHubConnectionBuilderUses(), []);
});
