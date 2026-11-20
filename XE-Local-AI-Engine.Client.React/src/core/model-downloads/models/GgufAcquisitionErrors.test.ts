import { t as i18nT, type TFunction } from "i18next";
import { describe, expect, it } from "vitest";

import { ggufFailureText, importErrorMessage } from "@/core/model-downloads/models/GgufAcquisitionErrors";
import en from "@/locales/en.json";

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

describe("GGUF acquisition failure text", () => {
	const download = (errorCode: string | null, sanitizedMessage: string | null = "Server text.") =>
		ggufFailureText(i18nT, { operationKind: "Download", errorCode, sanitizedMessage });

	it("translates every download code the coordinator and the Hugging Face client report", () => {
		expect(download("InsufficientStorage")).toBe("There is not enough free storage for this download.");
		expect(download("Interrupted")).toBe(
			"This download did not finish. Start it again to resume from the part already downloaded.",
		);
		for (const code of [
			"DestinationConflict",
			"ModelConflict",
			"DownloadCompensationFailed",
			"Gated",
			"Unauthorized",
			"DiskFull",
			"HashMismatch",
			"NotFound",
		]) {
			expect(download(code)).not.toContain("Server text.");
			expect(download(code)).not.toBe(en.pages.models.gguf.download.errors.DownloadFailed);
		}
	});

	it("keeps the server text as a second line only where it narrows the cause down", () => {
		expect(download("DownloadFailed", "Download failed.")).toBe("The model could not be downloaded.\nDownload failed.");
		expect(download("Network", "The model download failed with HTTP status 503. Please try again.")).toBe(
			"A network error stopped the download.\nThe model download failed with HTTP status 503. Please try again.",
		);
		expect(download("DownloadFailed", null)).toBe("The model could not be downloaded.");
	});

	it("falls back to the generic sentence, without the server text, for an unknown or missing code", () => {
		expect(download("SomethingNew")).toBe("The model could not be downloaded.");
		expect(download(null)).toBe("The model could not be downloaded.");
	});

	it("words a shared code for the import, and never adds the server text to it", () => {
		expect(ggufFailureText(i18nT, { operationKind: "Import", errorCode: "InsufficientStorage", sanitizedMessage: "x" })).toBe(
			"There is not enough free storage for this import.",
		);
		expect(ggufFailureText(i18nT, { operationKind: "Import", errorCode: "SomethingNew", sanitizedMessage: "x" })).toBe(
			"The model could not be imported.",
		);
	});
});
