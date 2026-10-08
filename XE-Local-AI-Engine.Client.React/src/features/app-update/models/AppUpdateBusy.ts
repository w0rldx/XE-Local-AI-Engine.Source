import type { TFunction } from "i18next";

import { ApiError } from "@/core/api/errors/ApiError";
import type { XeLocalAiEngineClientEndpointsAppUpdateV1AppUpdateBusyItemResponse } from "@/core/api/generated";

/** One piece of running work the update restart would stop, as the apply endpoint's 409 lists it. */
export type AppUpdateBusyItem = XeLocalAiEngineClientEndpointsAppUpdateV1AppUpdateBusyItemResponse;

const HTTP_CONFLICT = 409;

/**
 * Reads the running-work list a refused apply carries, or null for any other error. The shared axios interceptor
 * rethrows every non-2xx as `ApiError` with the body untouched, so the typed 409 body sits in `apiProblemDetails`.
 */
export function parseAppUpdateBusy(error: unknown): AppUpdateBusyItem[] | null {
	if (!(error instanceof ApiError) || error.statusCode !== HTTP_CONFLICT) {
		return null;
	}
	const items = (error.apiProblemDetails as unknown as Record<string, unknown> | undefined)?.["busyItems"];
	if (!Array.isArray(items)) {
		return null;
	}
	return items.map((value: unknown) => {
		const item = (value ?? {}) as Record<string, unknown>;
		return {
			kind: typeof item["kind"] === "string" ? item["kind"] : "",
			displayName: typeof item["displayName"] === "string" ? item["displayName"] : null,
		};
	});
}

// Every key is a literal so scripts/CheckI18nDefaults.mjs can see it; a template key would be invisible to it.
export function busyKindLabel(t: TFunction, kind: string): string {
	const labels: Record<string, string> = {
		trainingRun: t("pages.about.appUpdate.busy.kinds.trainingRun"),
		evaluationRun: t("pages.about.appUpdate.busy.kinds.evaluationRun"),
		trainingExport: t("pages.about.appUpdate.busy.kinds.trainingExport"),
		modelDownload: t("pages.about.appUpdate.busy.kinds.modelDownload"),
		imageModelDownload: t("pages.about.appUpdate.busy.kinds.imageModelDownload"),
		transcriptionModelDownload: t("pages.about.appUpdate.busy.kinds.transcriptionModelDownload"),
		llamaCppSourceBuild: t("pages.about.appUpdate.busy.kinds.llamaCppSourceBuild"),
		whisperCppSourceBuild: t("pages.about.appUpdate.busy.kinds.whisperCppSourceBuild"),
		stableDiffusionCppSourceBuild: t("pages.about.appUpdate.busy.kinds.stableDiffusionCppSourceBuild"),
		invocation: t("pages.about.appUpdate.busy.kinds.invocation"),
		workSession: t("pages.about.appUpdate.busy.kinds.workSession"),
	};
	return labels[kind] ?? t("pages.about.appUpdate.busy.kinds.unknown");
}
