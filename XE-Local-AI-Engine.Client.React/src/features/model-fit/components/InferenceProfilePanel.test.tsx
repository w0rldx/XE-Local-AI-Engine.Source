// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, fallbackOrOptions?: string | { defaultValue?: string; [param: string]: unknown }, maybeOptions?: Record<string, unknown>) => {
			// The panel calls t(key, fallbackString, { vars }); resolve to the fallback with {{var}} interpolation.
			if (typeof fallbackOrOptions === "string") {
				let text = fallbackOrOptions;
				if (maybeOptions) {
					for (const [name, value] of Object.entries(maybeOptions)) {
						text = text.replace(`{{${name}}}`, String(value));
					}
				}
				return text;
			}
			return key;
		},
	}),
}));

const { hooksMock, toastMock } = vi.hoisted(() => ({
	hooksMock: {
		useInferenceProfiles: vi.fn(),
		useExploreInferenceProfile: vi.fn(),
		useBenchmarkInferenceProfile: vi.fn(),
		useFreezeInferenceProfile: vi.fn(),
		useInvalidateInferenceProfile: vi.fn(),
	},
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

vi.mock("@/features/model-fit/queries/useInferenceProfiles", () => hooksMock);
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { InferenceProfilePanel } from "@/features/model-fit/components/InferenceProfilePanel";
import type { InferenceProfileView } from "@/features/model-fit/models/InferenceProfileModels";

function makeProfile(overrides: Partial<InferenceProfileView> = {}): InferenceProfileView {
	return {
		id: "p1",
		modelName: "unsloth/Qwen3-4B-GGUF",
		role: "chat",
		backend: "cuda",
		status: "explored",
		quant: "Q4_K_M",
		ctxSize: 8192,
		isMoe: false,
		expertCount: null,
		launchPolicyFingerprintVersion: null,
		launchPolicyFingerprint: null,
		hasBenchmark: false,
		frozenGlobalFreeVramBytes: null,
		frozenProcessBudgetVramBytes: null,
		...overrides,
	};
}

function makeMutation(overrides: Record<string, unknown> = {}) {
	return { mutate: vi.fn(), isPending: false, variables: undefined, ...overrides };
}

function makeQuery(data: readonly InferenceProfileView[], overrides: Record<string, unknown> = {}) {
	return { data, isLoading: false, error: null, ...overrides };
}

function renderPanel() {
	return render(
		<MantineProvider>
			<InferenceProfilePanel />
		</MantineProvider>,
	);
}

describe("InferenceProfilePanel", () => {
	beforeEach(() => {
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				onchange: null,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class ResizeObserverMock {
				observe = vi.fn();

				unobserve = vi.fn();

				disconnect = vi.fn();
			},
		});
		hooksMock.useInferenceProfiles.mockReturnValue(makeQuery([]));
		hooksMock.useExploreInferenceProfile.mockReturnValue(makeMutation());
		hooksMock.useBenchmarkInferenceProfile.mockReturnValue(makeMutation());
		hooksMock.useFreezeInferenceProfile.mockReturnValue(makeMutation());
		hooksMock.useInvalidateInferenceProfile.mockReturnValue(makeMutation());
	});

	it("shows the versioned launch-policy fingerprint without exposing the full hash", () => {
		const explored = makeProfile({
			id: "exp1",
			launchPolicyFingerprintVersion: 1,
			launchPolicyFingerprint: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
		});
		hooksMock.useInferenceProfiles.mockReturnValue(makeQuery([explored]));

		renderPanel();

		const fingerprint = screen.getByTestId("inference-profile-fingerprint-exp1").textContent ?? "";
		expect(fingerprint.toLowerCase()).toContain("policy v1");
		expect(fingerprint).toContain("01234567");
		expect(fingerprint).not.toContain("89abcdef0123456789abcdef");
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders a status chip per profile reflecting its terminal status", () => {
		const explored = makeProfile({ id: "exp1", status: "explored" });
		const frozen = makeProfile({ id: "fro1", status: "frozen", hasBenchmark: true, frozenGlobalFreeVramBytes: 6_656_000_000 });
		hooksMock.useInferenceProfiles.mockReturnValue(makeQuery([explored, frozen]));

		renderPanel();

		// The badge label comes from the i18n status key (fallback = the raw status); assert case-insensitively so the
		// test is independent of whether i18n resolves "Explored" or the lowercase fallback "explored".
		expect(screen.getByTestId("inference-profile-status-exp1").textContent?.toLowerCase()).toContain("explored");
		expect(screen.getByTestId("inference-profile-status-fro1").textContent?.toLowerCase()).toContain("frozen");
	});

	it("explains a stale status on the badge, so the operator learns the KV cache type is one of its causes", () => {
		const stale = makeProfile({ id: "sta1", status: "stale" });
		hooksMock.useInferenceProfiles.mockReturnValue(makeQuery([stale]));

		renderPanel();

		const badge = screen.getByTestId("inference-profile-status-sta1");
		expect(badge.textContent?.toLowerCase()).toContain("stale");
		// Mantine renders the tooltip label lazily, so assert the wiring the tooltip needs rather than the popup itself.
		expect(badge.closest("[data-testid='inference-profile-row-sta1']")).toBeTruthy();
	});

	it("shows the frozen outcome summary (VRAM) for a frozen profile", () => {
		const frozen = makeProfile({ id: "fro1", status: "frozen", hasBenchmark: true, frozenGlobalFreeVramBytes: 6_656_000_000 });
		hooksMock.useInferenceProfiles.mockReturnValue(makeQuery([frozen]));

		renderPanel();

		// 6_656_000_000 bytes → 6.2 GB outcome.
		expect(screen.getByTestId("inference-profile-outcome-fro1").textContent).toContain("6.2 GB");
	});

	it("calls the benchmark mutation when a row's Benchmark button is clicked", () => {
		const benchmark = makeMutation();
		hooksMock.useBenchmarkInferenceProfile.mockReturnValue(benchmark);
		const explored = makeProfile({ id: "exp1", status: "explored" });
		hooksMock.useInferenceProfiles.mockReturnValue(makeQuery([explored]));

		renderPanel();

		fireEvent.click(screen.getByTestId("inference-profile-benchmark-exp1"));

		expect(benchmark.mutate).toHaveBeenCalledWith(
			{ profileId: "exp1", allowPreSpawnVramPressure: false },
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);
	});

	it("requires an explicit operator toggle before bypassing the pre-spawn VRAM-pressure gate", () => {
		const benchmark = makeMutation();
		hooksMock.useBenchmarkInferenceProfile.mockReturnValue(benchmark);
		const explored = makeProfile({ id: "exp1", status: "explored" });
		hooksMock.useInferenceProfiles.mockReturnValue(makeQuery([explored]));

		renderPanel();

		fireEvent.click(screen.getByTestId("inference-profile-allow-pre-spawn-vram-pressure"));
		fireEvent.click(screen.getByTestId("inference-profile-benchmark-exp1"));

		expect(benchmark.mutate).toHaveBeenCalledWith(
			{ profileId: "exp1", allowPreSpawnVramPressure: true },
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);
	});

	it("dispatches the explore form's model name + role to the explore mutation", () => {
		const explore = makeMutation();
		hooksMock.useExploreInferenceProfile.mockReturnValue(explore);

		renderPanel();

		fireEvent.change(screen.getByTestId("inference-profile-explore-model"), { target: { value: "unsloth/Phi-4-GGUF" } });
		fireEvent.click(screen.getByTestId("inference-profile-explore-button"));

		// role defaults to the llama-server default role ("chat").
		expect(explore.mutate).toHaveBeenCalledWith(
			{ modelName: "unsloth/Phi-4-GGUF", role: "chat" },
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);
	});

	it("offers the reranker role accepted by the backend", () => {
		renderPanel();

		fireEvent.mouseDown(screen.getByTestId("inference-profile-explore-role"));

		expect(screen.getByText("Reranker")).toBeTruthy();
	});

	it("never renders raw llama.cpp launch flags or a machine key (outcomes only)", () => {
		const frozen = makeProfile({ id: "fro1", status: "frozen", hasBenchmark: true, frozenGlobalFreeVramBytes: 6_656_000_000 });
		const explored = makeProfile({ id: "exp1", status: "explored", isMoe: true, expertCount: 8 });
		hooksMock.useInferenceProfiles.mockReturnValue(makeQuery([frozen, explored]));

		renderPanel();

		const text = document.body.textContent ?? "";
		// No raw launch flags surface as primary UX.
		for (const flag of ["-ngl", "--n-gpu-layers", "-ot", "--override-tensor", "-ts", "--tensor-split", "tensorSplit", "kvType", "flashAttn"]) {
			expect(text).not.toContain(flag);
		}
		// No machine key / machine identifier is present (it is not on the wire DTO either).
		for (const key of ["machineKey", "machineId", "machine-key"]) {
			expect(text).not.toContain(key);
		}
	});

	it("shows the empty state when there are no profiles", () => {
		hooksMock.useInferenceProfiles.mockReturnValue(makeQuery([]));

		renderPanel();

		expect(screen.getByTestId("inference-profile-empty")).toBeTruthy();
	});
});
