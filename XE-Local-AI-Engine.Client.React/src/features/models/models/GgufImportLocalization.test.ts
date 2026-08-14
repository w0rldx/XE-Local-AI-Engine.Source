import { describe, expect, it } from "vitest";

import de from "@/locales/de.json";
import en from "@/locales/en.json";

describe("GGUF import localization", () => {
	it("provides English and German labels for the dialog, progress, provenance, and safe errors", () => {
		expect(en.pages.models.gguf.import.sourcePath).toBe("GGUF file path");
		expect(de.pages.models.gguf.import.sourcePath).toBe("Pfad zur GGUF-Datei");
		expect(en.pages.models.gguf.import.errors.SourceNotFound).not.toContain("/");
		expect(de.pages.models.gguf.import.errors.SourceNotFound).toContain("nicht gefunden");
		expect(en.pages.models.local.origin.imported).toBe("Imported");
		expect(de.pages.models.local.origin.imported).toBe("Importiert");
	});
});
