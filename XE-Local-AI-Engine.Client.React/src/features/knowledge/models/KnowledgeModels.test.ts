import { describe, expect, it } from "vitest";

import {
	isAcceptedKnowledgeFile,
	KNOWLEDGE_ACCEPT_ATTRIBUTE,
	KNOWLEDGE_ACCEPTED_EXTENSIONS,
	KNOWLEDGE_DETERMINISTIC_TEXT_EXTENSIONS,
	normalizeKnowledgeCollectionId,
} from "@/features/knowledge/models/KnowledgeModels";

describe("normalizeKnowledgeCollectionId", () => {
	it("normalizes a repository namespace without changing its safe punctuation", () => {
		expect(normalizeKnowledgeCollectionId("  repo-xe.engine_1  ")).toBe("REPO-XE.ENGINE_1");
	});

	it.each(["", "project/name", "spaces are not allowed", "x".repeat(129)])("rejects invalid namespace %s", (value) => {
		expect(normalizeKnowledgeCollectionId(value)).toBeUndefined();
	});
});

describe("isAcceptedKnowledgeFile", () => {
	// Mirrors PlaintextDocumentReader.SupportedExtensions. This intentionally enumerates the backend contract rather
	// than sampling a few languages, so adding a deterministic reader format without updating the picker fails loudly.
	const backendDeterministicTextExtensions = [
		".txt",
		".text",
		".md",
		".markdown",
		".csv",
		".tsv",
		".json",
		".jsonc",
		".log",
		".cs",
		".ts",
		".tsx",
		".js",
		".jsx",
		".mjs",
		".cjs",
		".py",
		".java",
		".go",
		".rs",
		".cpp",
		".cc",
		".cxx",
		".c",
		".h",
		".hpp",
		".hh",
		".html",
		".htm",
		".xml",
		".xaml",
		".yaml",
		".yml",
		".toml",
		".ini",
		".cfg",
		".conf",
		".properties",
		".env",
		".sh",
		".bash",
		".zsh",
		".ps1",
		".bat",
		".sql",
		".css",
		".scss",
		".sass",
		".less",
		".rb",
		".php",
		".kt",
		".kts",
		".swift",
		".scala",
		".pl",
		".lua",
		".r",
		".vb",
		".fs",
		".fsx",
		".gradle",
		".dockerfile",
		".gitignore",
		".editorconfig",
	] as const;

	it("keeps deterministic plaintext/code extensions in exact backend parity", () => {
		expect(KNOWLEDGE_DETERMINISTIC_TEXT_EXTENSIONS).toEqual(backendDeterministicTextExtensions);
	});

	it.each([
		"README.md",
		"Widget.cs",
		"route.tsx",
		"worker.py",
		"main.go",
		"lib.rs",
		"trace.log",
		"Dockerfile.dockerfile",
	])("accepts deterministic prose/code ingestion for %s", (fileName) => {
		expect(isAcceptedKnowledgeFile(fileName)).toBe(true);
	});

	it("uses the complete advisory set in the native file picker", () => {
		expect(KNOWLEDGE_ACCEPT_ATTRIBUTE.split(",")).toEqual(KNOWLEDGE_ACCEPTED_EXTENSIONS);
		expect(KNOWLEDGE_ACCEPTED_EXTENSIONS).toContain(".pdf");
		expect(KNOWLEDGE_ACCEPTED_EXTENSIONS).toContain(".docx");
	});

	it("continues to reject binary executables", () => {
		expect(isAcceptedKnowledgeFile("tool.exe")).toBe(false);
	});
});
