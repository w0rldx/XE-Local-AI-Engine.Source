import type { TFunction } from "i18next";

import type { GgufAcquisitionStatus } from "@/core/model-downloads/models/GgufAcquisitionModels";

// The server's stable GGUF acquisition failure codes as translated text. An import and a download share some codes
// (ModelConflict, DestinationConflict, InsufficientStorage) but each words them for its own operation; an unknown code
// falls back to the operation's generic failure sentence.
const knownImportErrorCodes = new Set([
	"InvalidPath",
	"InvalidRequest",
	"SourceNotFound",
	"UnsupportedFileType",
	"UnsupportedGgufVersion",
	"UnsupportedModelKind",
	"UnsupportedArchitecture",
	"UnsupportedQuantization",
	"ModelConflict",
	"DestinationConflict",
	"AcquisitionAlreadyActive",
	"InsufficientStorage",
	"InvalidPreviewToken",
	"StalePreview",
	"OperationNotFound",
	"ImportCompensationFailed",
	"ImportFailed",
]);

export function importErrorCodeFrom(error: unknown): string | undefined {
	if (!error || typeof error !== "object") {
		return undefined;
	}
	const response = "response" in error && error.response && typeof error.response === "object" ? error.response : undefined;
	const data = response && "data" in response && response.data && typeof response.data === "object" ? response.data : undefined;
	const code = data && "errorCode" in data && typeof data.errorCode === "string" ? data.errorCode : undefined;
	return code && knownImportErrorCodes.has(code) ? code : undefined;
}

export function importErrorMessage(t: TFunction, errorCode: string | null | undefined): string {
	const safeCode = errorCode && knownImportErrorCodes.has(errorCode) ? errorCode : "ImportFailed";
	return t(`pages.models.gguf.import.errors.${safeCode}`, {
		defaultValue: t("pages.models.gguf.import.errors.ImportFailed", "The model could not be imported."),
	});
}

// GgufDownloadCoordinator's own codes plus the HuggingFaceDownloadFailure reasons it passes through.
const knownDownloadErrorCodes = new Set([
	"DestinationConflict",
	"InsufficientStorage",
	"ModelConflict",
	"DownloadFailed",
	"DownloadCompensationFailed",
	"Interrupted",
	"Network",
	"Gated",
	"Unauthorized",
	"DiskFull",
	"HashMismatch",
	"NotFound",
]);

// Codes whose server text differs per failure (an HTTP status, a timeout, a dropped range), so it follows on a second
// line; every surface renders the text with `white-space: pre-line`.
const downloadCodesWithReason = new Set(["DownloadFailed", "Network"]);

/** A failed GGUF download's or import's text, the same on the feed, its toast and the Models and Home panels. */
export function ggufFailureText(
	t: TFunction,
	status: Pick<GgufAcquisitionStatus, "operationKind" | "errorCode" | "sanitizedMessage">,
): string {
	if (status.operationKind === "Import") {
		return importErrorMessage(t, status.errorCode);
	}
	const code = status.errorCode && knownDownloadErrorCodes.has(status.errorCode) ? status.errorCode : "DownloadFailed";
	const message = t(`pages.models.gguf.download.errors.${code}`, {
		defaultValue: t("pages.models.gguf.download.errors.DownloadFailed", "The model could not be downloaded."),
	});
	const reason = downloadCodesWithReason.has(status.errorCode ?? "") ? status.sanitizedMessage?.trim() || undefined : undefined;
	return reason === undefined ? message : `${message}\n${reason}`;
}
