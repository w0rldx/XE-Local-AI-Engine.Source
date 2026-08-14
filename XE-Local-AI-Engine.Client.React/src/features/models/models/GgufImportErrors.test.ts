import type { TFunction } from "i18next";
import { describe, expect, it } from "vitest";

import en from "@/locales/en.json";
import { importErrorMessage } from "@/features/models/models/GgufImportErrors";

function translate(key: string): string {
	const prefix = "pages.models.gguf.import.errors.";
	if (key.startsWith(prefix)) {
		const errorCode = key.slice(prefix.length) as keyof typeof en.pages.models.gguf.import.errors;
		return en.pages.models.gguf.import.errors[errorCode];
	}
	return key;
}

describe("GGUF import safe errors", () => {
	it("maps stable server codes and fails closed for unknown values", () => {
		const t = translate as TFunction;
		expect(importErrorMessage(t, "SourceNotFound")).toBe("The selected GGUF file was not found.");
		expect(importErrorMessage(t, "UnknownServerMessage")).toBe("The model could not be imported.");
	});

	it("maps the transaction-coordinator error codes to non-generic messages", () => {
		const t = translate as TFunction;
		for (const code of ["InvalidRequest", "InvalidPreviewToken", "StalePreview", "ImportCompensationFailed"]) {
			expect(importErrorMessage(t, code)).not.toBe("The model could not be imported.");
		}
	});
});
