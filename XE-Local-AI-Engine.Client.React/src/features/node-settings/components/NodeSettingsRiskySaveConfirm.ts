import type { TFunction } from "i18next";

import type { XeLocalAiEngineClientEndpointsNodeSettingsV1SaveNodeSettingsRequest as SaveNodeSettingsRequest } from "@/core/api/generated";
import type { ConfirmContextType } from "@/core/ui/models/Confirm";
import { lowersSandboxSecurityProfile, turnsOnExecutionPreviews } from "@/features/node-settings/models/NodeSettingsSaveRequest";

// Asks the operator about the changes in a save that widen what a workload may do. False means they declined: the caller
// keeps the whole draft, so nothing else they edited is lost.
export async function confirmRiskySave(
	body: SaveNodeSettingsRequest,
	confirm: ConfirmContextType["confirm"],
	t: TFunction,
): Promise<boolean> {
	// Turning execution previews on widens what a sandboxed workload may run under, so the operator confirms it knowing the
	// residual risk.
	if (
		turnsOnExecutionPreviews(body) &&
		!(await confirm({
			title: t("pages.nodeSettings.executionPreviewsConfirm.title", "Turn on execution previews?"),
			description: t(
				"pages.nodeSettings.executionPreviewsConfirm.description",
				"Preview sandbox mechanisms will run Python and sandboxed MCP servers. On Windows that is the AppContainer boundary. The code cannot read the engine's data or your files unless a folder allows all app packages. It can show windows, use the Windows UI kernel interface and read what is on your screen, but not the clipboard or simulated input. It runs with no CPU, memory or process-count limit, only the time limit, and it can see host path names. Turn this on only if you accept that.",
			),
			confirmationText: t("pages.nodeSettings.executionPreviewsConfirm.confirm", "Turn on"),
			cancellationText: t("pages.nodeSettings.executionPreviewsConfirm.cancel", "Cancel"),
		}))
	) {
		return false;
	}
	// Lowering the profile turns every declared boundary back into a preference, so a workload this host cannot isolate
	// runs with less instead of refusing. Raising it narrows nothing and is never asked about.
	if (
		lowersSandboxSecurityProfile(body) &&
		!(await confirm({
			title: t("pages.nodeSettings.sandboxProfileLowerConfirm.title", "Lower the sandbox security profile?"),
			description: t(
				"pages.nodeSettings.sandboxProfileLowerConfirm.description",
				"With the Low profile, a workload that asks for a boundary this host cannot provide runs without it instead of refusing to start. The safety floors every profile keeps still apply. Lower it only if you accept that.",
			),
			confirmationText: t("pages.nodeSettings.sandboxProfileLowerConfirm.confirm", "Lower to Low"),
			cancellationText: t("pages.nodeSettings.sandboxProfileLowerConfirm.cancel", "Cancel"),
		}))
	) {
		return false;
	}
	return true;
}
