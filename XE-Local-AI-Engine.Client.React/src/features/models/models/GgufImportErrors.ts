import type { TFunction } from "i18next";

const knownImportErrorCodes = new Set([
	"InvalidPath",
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
	"OperationNotFound",
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
