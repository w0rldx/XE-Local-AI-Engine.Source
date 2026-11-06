import type { TFunction } from "i18next";

// The server sends the runtime audit's CPU-fallback reason and remediation and its undetermined-backend reason in English
// beside stable codes; the UI localizes from the codes. A code this client does not know (a newer server) gets the
// generic sentence and no detail line, never English.

export function backendUndeterminedDetailText(t: TFunction, code: string | null): string | null {
	switch (code) {
		case "runtime_missing":
			return t(
				"pages.modelFit.hardware.backendUndeterminedAlert.details.runtimeMissing",
				"No llama.cpp runtime is installed yet. It is downloaded when you install it from Node Settings, or the first time you start a chat, benchmark or training run; this page never downloads it by itself.",
			);
		case "companion_libraries_incomplete":
			return t(
				"pages.modelFit.hardware.backendUndeterminedAlert.details.companionLibrariesIncomplete",
				"The llama.cpp runtime is installed, but some of its CUDA companion libraries are missing, so it cannot use the GPU. Models load on the CPU until it is repaired. Retry the runtime download from Node Settings, or start a chat, to repair it.",
			);
		case "probe_failed":
			return t(
				"pages.modelFit.hardware.backendUndeterminedAlert.details.probeFailed",
				"Common causes: a bring-your-own XE_LLAMACPP_SERVER_PATH override that was rejected because it exposes no GPU device for its configured variant (check the log for the override's own error), a runtime whose libraries could not be loaded, or a busy GPU driver that made the probe overrun. Refreshing the hardware profile re-runs the probe.",
			);
		default:
			return null;
	}
}

export function cpuFallbackReasonText(t: TFunction, code: string | null): string {
	switch (code) {
		case "cpu_variant":
			return t(
				"pages.modelFit.hardware.cpuFallbackAlert.reasons.cpuVariant",
				"A GPU was detected, but the CPU llama.cpp runtime is selected, so inference is running on the CPU.",
			);
		case "zero_devices":
			return t(
				"pages.modelFit.hardware.cpuFallbackAlert.reasons.zeroDevices",
				"The selected llama.cpp GPU runtime found no GPU devices, so inference is running on the CPU.",
			);
		default:
			return t(
				"pages.modelFit.hardware.cpuFallbackAlert.reason",
				"The selected inference runtime could not use this machine's GPU, so inference is running on the CPU.",
			);
	}
}

export function cpuFallbackRemediationText(t: TFunction, code: string | null): string | null {
	switch (code) {
		case "windows":
			return t(
				"pages.modelFit.hardware.cpuFallbackAlert.remediations.windows",
				"Update the GPU driver, check the GPU is visible to the system, then reinstall the runtime from Node Settings. If a bring-your-own override is set, verify XE_LLAMACPP_SERVER_PATH points to a valid GPU-capable binary.",
			);
		case "linux_nvidia":
			return t(
				"pages.modelFit.hardware.cpuFallbackAlert.remediations.linuxNvidia",
				"In Node Settings, use automatic variant selection (or install the CUDA variant) so the managed CUDA prebuilt serves; it needs the NVIDIA driver 525 series or newer. Otherwise build the Vulkan runtime with the in-app source build, choosing the Vulkan backend, which then takes precedence over the prebuilt; that needs a Vulkan ICD (commonly missing under WSL2). If a bring-your-own override is set, verify XE_LLAMACPP_SERVER_PATH points to a valid GPU-capable binary.",
			);
		case "linux_other":
			return t(
				"pages.modelFit.hardware.cpuFallbackAlert.remediations.linuxOther",
				"In Node Settings, use automatic variant selection if a variant was pinned; the managed Vulkan runtime needs a GPU driver with a Vulkan ICD (commonly missing under WSL2). Otherwise build the Vulkan runtime with the in-app source build, choosing the Vulkan backend, which then takes precedence over the prebuilt; that needs a Vulkan ICD (commonly missing under WSL2). If a bring-your-own override is set, verify XE_LLAMACPP_SERVER_PATH points to a valid GPU-capable binary.",
			);
		default:
			return null;
	}
}
