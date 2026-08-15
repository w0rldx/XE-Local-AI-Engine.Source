/**
 * The Monaco runtime, assembled once and only ever reached through `import("./MonacoRuntime")` from `CodeEditor` so the
 * ~3 MB editor core stays in its own lazily-fetched chunk (see `config/bundle-budget.json` → `lazyEditorJavaScriptBytes`).
 *
 * Deliberately `editor.api` (the core) plus a hand-picked set of Monarch grammars — NOT `editor.main`, which drags in
 * every language plus the TypeScript/JSON/CSS/HTML language services and their workers (measured: the JSON service alone
 * is +1.6 MB). Highlighting is what the viewer needs; a language service is a follow-up for the feature that wants it.
 * Workers are bundled through Vite's `?worker` import so nothing is fetched from a CDN — the app must work offline.
 */
import * as monaco from "monaco-editor/editor/editor.api.js";
import EditorWorker from "monaco-editor/editor/editor.worker.js?worker";
import "monaco-editor/languages/definitions/csharp/register.js";
import "monaco-editor/languages/definitions/dockerfile/register.js";
import "monaco-editor/languages/definitions/ini/register.js";
import "monaco-editor/languages/definitions/javascript/register.js";
import "monaco-editor/languages/definitions/markdown/register.js";
import "monaco-editor/languages/definitions/powershell/register.js";
import "monaco-editor/languages/definitions/python/register.js";
import "monaco-editor/languages/definitions/shell/register.js";
import "monaco-editor/languages/definitions/sql/register.js";
import "monaco-editor/languages/definitions/typescript/register.js";
import "monaco-editor/languages/definitions/xml/register.js";
import "monaco-editor/languages/definitions/yaml/register.js";

globalThis.MonacoEnvironment = {
	// Only the generic editor worker ships (word-based suggestions, diff computation). Language-service workers
	// (json/ts/css/html) are not bundled, so every label maps to it.
	getWorker: () => new EditorWorker(),
};

// Monaco has no built-in unified-diff grammar and JSON only ships as a worker-backed language service. Two small
// Monarch grammars cover both view-only needs.
monaco.languages.register({ id: "diff", extensions: [".diff", ".patch"], aliases: ["Diff", "Patch"] });
monaco.languages.setMonarchTokensProvider("diff", {
	tokenizer: {
		root: [
			[/^(?:\+\+\+|---) .*$/, "diff.header"],
			[/^(?:diff|index|new file|deleted file|old mode|new mode|rename|similarity|Binary) .*$/, "diff.header"],
			[/^@@.*$/, "diff.hunk"],
			[/^\+.*$/, "diff.added"],
			[/^-.*$/, "diff.removed"],
			[/^\\ No newline.*$/, "comment"],
		],
	},
});

monaco.languages.register({ id: "json", extensions: [".json"], aliases: ["JSON"] });
monaco.languages.setMonarchTokensProvider("json", {
	tokenizer: {
		root: [
			[/"(?:[^"\\]|\\.)*"(?=\s*:)/, "type"],
			[/"(?:[^"\\]|\\.)*"/, "string"],
			[/-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/, "number"],
			[/\b(?:true|false|null)\b/, "keyword"],
			[/[{}[\],:]/, "delimiter"],
		],
	},
});
monaco.languages.setLanguageConfiguration("json", {
	brackets: [
		["{", "}"],
		["[", "]"],
	],
	autoClosingPairs: [{ open: "{", close: "}" }, { open: "[", close: "]" }, { open: '"', close: '"' }],
});

const diffRules = (added: string, removed: string, hunk: string): monaco.editor.ITokenThemeRule[] => [
	{ token: "diff.added", foreground: added },
	{ token: "diff.removed", foreground: removed },
	{ token: "diff.hunk", foreground: hunk },
	{ token: "diff.header", fontStyle: "bold" },
];
monaco.editor.defineTheme("xe-light", { base: "vs", inherit: true, rules: diffRules("22863A", "B31D28", "005CC5"), colors: {} });
monaco.editor.defineTheme("xe-dark", { base: "vs-dark", inherit: true, rules: diffRules("85E89D", "FDAEB7", "79B8FF"), colors: {} });

export { monaco };
