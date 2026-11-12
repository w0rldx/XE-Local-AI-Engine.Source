import { describe, expect, it } from "vitest";

import { DOCUMENT_ATTACHMENT_EXTENSIONS } from "@/features/chat/models/AttachmentAccept";

import { readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

// The server's extractor sources, read from the repository so a change there without the SPA mirror fails here.
const repoRoot = join(dirname(fileURLToPath(import.meta.url)), "../../../../..");
const extractionDir = join(repoRoot, "XE-Local-AI-Engine.Client.Application/Services/DocumentIngestion");

async function serverExtensions(): Promise<string[]> {
	const plaintext = await readFile(join(extractionDir, "Extraction/PlaintextDocumentReader.cs"), "utf8");
	const extractor = await readFile(join(extractionDir, "Implementation/DocumentTextExtractor.cs"), "utf8");
	const listBody = /SupportedExtensions\s*\{\s*get;\s*\}\s*=\s*\[([^\]]*)\]/.exec(plaintext)?.[1] ?? "";
	const plaintextExtensions = [...listBody.matchAll(/"(\.[^"]+)"/g)].map((match) => match[1] ?? "");
	const otherReaders = [...extractor.matchAll(/readers\["(\.[^"]+)"\]/g)].map((match) => match[1] ?? "");
	return [...plaintextExtensions, ...otherReaders];
}

describe("DOCUMENT_ATTACHMENT_EXTENSIONS", () => {
	it("mirrors exactly the extensions the server's document extractor accepts", async () => {
		const server = await serverExtensions();

		expect(server.length).toBeGreaterThan(10);
		expect(server).toContain(".pdf");
		expect(new Set(DOCUMENT_ATTACHMENT_EXTENSIONS)).toEqual(new Set(server));
	});
});
