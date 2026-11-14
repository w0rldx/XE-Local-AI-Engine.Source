// @vitest-environment jsdom

import { cleanup, render } from "@testing-library/react";
import { StrictMode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { GgufAcquisitionStatus } from "@/core/model-downloads/models/GgufAcquisitionModels";
import { OperationToastCoordinator } from "@/core/operations/components/OperationToastCoordinator";
import { useJobNotificationStore } from "@/core/operations/stores/JobNotificationStore";
import type { RuntimeAcquisitionStatus } from "@/core/runtime-acquisition/queries/useRuntimeAcquisitionStatus";
import { toast } from "@/core/ui/notifications/Toast";
import { createProvidersWrapper, createTestQueryClient, renderWithProviders } from "@/test/RenderWithProviders";

// The two source feeds are hub-backed hooks with their own tests; here they are the values the coordinator watches.
// Everything between them and the toast (merge, normalization, transition rule) runs for real.
const sources = vi.hoisted(() => ({
	gguf: new Map<string, unknown>(),
	runtime: undefined as unknown,
}));

vi.mock("@/core/model-downloads/queries/useGgufAcquisitions", () => ({
	useActiveGgufAcquisitions: () => sources.gguf,
}));
vi.mock("@/core/runtime-acquisition/hooks/useRuntimeAcquisitionHub", () => ({
	useRuntimeAcquisitionHub: () => sources.runtime,
}));

function download(phase: GgufAcquisitionStatus["phase"], overrides: Partial<GgufAcquisitionStatus> = {}) {
	sources.gguf = new Map([
		[
			"op-1",
			{
				operationId: "op-1",
				operationKind: "Download",
				modelName: "qwen3-8b",
				phase,
				pct: undefined,
				completedBytes: null,
				totalBytes: null,
				startedAtUtc: null,
				updatedAtUtc: null,
				errorCode: null,
				sanitizedMessage: null,
				...overrides,
			} satisfies GgufAcquisitionStatus,
		],
	]);
}

function runtimePhase(sequence: number, phase: string, sanitizedError?: string) {
	sources.runtime = {
		sequence,
		phase,
		variant: "Cpu",
		tag: "b9692",
		completedBytes: null,
		totalBytes: null,
		stepIndex: 1,
		stepCount: 1,
		sanitizedError,
	} satisfies RuntimeAcquisitionStatus;
}

let success: ReturnType<typeof vi.spyOn>;
let error: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
	sources.gguf = new Map();
	sources.runtime = undefined;
	success = vi.spyOn(toast, "success").mockImplementation(() => undefined);
	error = vi.spyOn(toast, "error").mockImplementation(() => undefined);
});

afterEach(() => {
	cleanup();
	vi.restoreAllMocks();
});

// A source change re-renders the coordinator, exactly as a hub push or a re-hydration would.
function renderCoordinator(queryClient = createTestQueryClient()) {
	const { wrapper } = createProvidersWrapper({ queryClient });
	const result = render(<OperationToastCoordinator />, { wrapper });
	return { ...result, update: () => result.rerender(<OperationToastCoordinator />) };
}

describe("OperationToastCoordinator", () => {
	it("toasts success exactly once when a watched download completes, and not again on a replay", () => {
		download("Downloading");
		const { update } = renderCoordinator();

		download("Completed", { pct: 100 });
		update();
		// A reconnect re-hydration hands the same terminal row back as a new snapshot.
		download("Completed", { pct: 100 });
		update();

		expect(success).toHaveBeenCalledOnce();
		expect(success).toHaveBeenCalledWith("qwen3-8b is downloaded", { id: "operation:gguf-download:op-1:succeeded" });
		expect(error).not.toHaveBeenCalled();
	});

	it("toasts the failure exactly once, with the reason under the operation's title", () => {
		runtimePhase(1, "Downloading");
		const { update } = renderCoordinator();

		runtimePhase(2, "Failed", "Offline.");
		update();
		update();

		expect(error).toHaveBeenCalledOnce();
		expect(error).toHaveBeenCalledWith("Offline.", {
			id: "operation:runtime-acquisition:llamacpp:failed",
			title: "The local AI runtime could not be installed",
		});
	});

	it("stays silent for a terminal state already present at first hydration", () => {
		download("Completed");
		runtimePhase(4, "Failed");
		const { update } = renderCoordinator();
		update();

		expect(success).not.toHaveBeenCalled();
		expect(error).not.toHaveBeenCalled();
	});

	it("toasts once under StrictMode's doubled effects and stays silent after a remount", () => {
		const { wrapper } = createProvidersWrapper();
		const strict = render(<OperationToastCoordinator />, {
			wrapper: ({ children }) => <StrictMode>{wrapper({ children })}</StrictMode>,
		});
		download("Downloading");
		strict.rerender(<OperationToastCoordinator />);
		download("Completed");
		strict.rerender(<OperationToastCoordinator />);
		strict.unmount();

		renderWithProviders(<OperationToastCoordinator />);

		expect(success).toHaveBeenCalledOnce();
	});

	function runtimeStatusReads(invalidate: ReturnType<typeof vi.spyOn>) {
		return invalidate.mock.calls.filter(
			([filters]: unknown[]) => JSON.stringify(filters) === JSON.stringify({ queryKey: [{ _id: "getLlamaCppRuntime" }] }),
		).length;
	}

	it("re-reads the runtime status once and toasts once when the runtime acquisition completes", () => {
		const queryClient = createTestQueryClient();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries");
		runtimePhase(1, "Extracting");
		const { update } = renderCoordinator(queryClient);

		runtimePhase(2, "Completed");
		update();

		expect(success).toHaveBeenCalledOnce();
		expect(success).toHaveBeenCalledWith("The local AI runtime is installed", {
			id: "operation:runtime-acquisition:llamacpp:succeeded",
		});
		expect(runtimeStatusReads(invalidate)).toBe(1);
	});

	it("re-reads the runtime status without a toast when the acquisition is first seen already completed", () => {
		const queryClient = createTestQueryClient();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries");
		runtimePhase(3, "Completed");

		renderCoordinator(queryClient);

		expect(runtimeStatusReads(invalidate)).toBe(1);
		expect(success).not.toHaveBeenCalled();
	});

	describe("alerts for a user who is elsewhere", () => {
		let visibility: DocumentVisibilityState;
		const shown: { title: string; options?: NotificationOptions }[] = [];
		class FakeNotification {
			static permission: NotificationPermission = "granted";
			onclick: (() => void) | null = null;
			constructor(title: string, options?: NotificationOptions) {
				shown.push({ title, options });
			}
			close = vi.fn();
		}

		beforeEach(() => {
			visibility = "visible";
			vi.spyOn(document, "visibilityState", "get").mockImplementation(() => visibility);
			document.title = "XE Local AI Engine";
			shown.length = 0;
			FakeNotification.permission = "granted";
			vi.stubGlobal("Notification", FakeNotification);
			useJobNotificationStore.getState().actions.setSystemNotifications(false);
		});

		afterEach(() => {
			vi.unstubAllGlobals();
			window.history.replaceState(null, "", "/");
		});

		function finishDownload(): ReturnType<typeof renderCoordinator> {
			download("Downloading");
			const rendered = renderCoordinator();
			download("Completed");
			rendered.update();
			return rendered;
		}

		it("stays silent on the operation's own page while the tab is visible", () => {
			window.history.replaceState(null, "", "/models");

			finishDownload();

			expect(success).not.toHaveBeenCalled();
			expect(document.title).toBe("XE Local AI Engine");
		});

		it("toasts on another page without touching the title while the tab is visible", () => {
			finishDownload();

			expect(success).toHaveBeenCalledOnce();
			expect(document.title).toBe("XE Local AI Engine");
		});

		it("in a hidden tab toasts even on the own page, counts into the title and clears it once the tab is seen", () => {
			window.history.replaceState(null, "", "/models");
			visibility = "hidden";

			finishDownload();

			expect(success).toHaveBeenCalledOnce();
			expect(document.title).toBe("(1) XE Local AI Engine");

			visibility = "visible";
			document.dispatchEvent(new Event("visibilitychange"));
			expect(document.title).toBe("XE Local AI Engine");
		});

		it("shows no OS notification until the user turned them on", () => {
			visibility = "hidden";

			finishDownload();

			expect(shown).toEqual([]);
		});

		it("shows an OS notification for a hidden-tab finish once turned on and permitted", () => {
			useJobNotificationStore.getState().actions.setSystemNotifications(true);
			visibility = "hidden";

			finishDownload();

			expect(shown).toEqual([{ title: "qwen3-8b is downloaded", options: { body: undefined, tag: "gguf-download:op-1" } }]);
		});

		it("shows no OS notification when the browser denied the permission, or while the tab is visible", () => {
			useJobNotificationStore.getState().actions.setSystemNotifications(true);
			finishDownload();
			cleanup();
			FakeNotification.permission = "denied";
			visibility = "hidden";
			finishDownload();

			expect(shown).toEqual([]);
			expect(success).toHaveBeenCalledTimes(2);
		});
	});

	it("does not re-read the runtime status again while the acquisition stays completed", () => {
		const queryClient = createTestQueryClient();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries");
		runtimePhase(3, "Completed");
		const { update } = renderCoordinator(queryClient);

		runtimePhase(3, "Completed");
		update();
		update();

		expect(runtimeStatusReads(invalidate)).toBe(1);
	});
});
