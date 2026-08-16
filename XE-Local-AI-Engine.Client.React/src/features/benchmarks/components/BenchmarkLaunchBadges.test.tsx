// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { BenchmarkLaunchBadges } from "@/features/benchmarks/components/BenchmarkLaunchBadges";
import type { BenchmarkLaunchFacts } from "@/features/benchmarks/models/BenchmarkModels";
import { noBenchmarkLaunchFacts } from "@/features/benchmarks/models/BenchmarkModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The badge row is the only place an operator sees what actually launched, so each fact must read differently: an
// automatic KV pick from an explicit one, a GPU launch from a silent fall back to CPU, and a run whose backend the
// node could not determine from one it knows. A run frozen before the receipt existed shows "—" rather than a guess.

afterEach(cleanup);

const facts = (overrides: Partial<BenchmarkLaunchFacts>): BenchmarkLaunchFacts => ({ ...noBenchmarkLaunchFacts, ...overrides });

const renderBadges = (launch: BenchmarkLaunchFacts) => renderWithProviders(<BenchmarkLaunchBadges launch={launch} />);

describe("BenchmarkLaunchBadges", () => {
	it("marks an automatic KV pick, flash attention, and a fully offloaded GPU launch", () => {
		renderBadges(
			facts({
				kvCacheType: "q8_0",
				kvCacheTypeSource: "auto",
				flashAttentionMode: "on",
				effectiveBackend: "cuda",
				placementOffloaded: 32,
				placementTotal: 32,
			}),
		);

		expect(screen.getByText("KV q8_0 (auto)")).toBeTruthy();
		expect(screen.getByText("FA on")).toBeTruthy();
		expect(screen.getByText("CUDA 32/32 layers")).toBeTruthy();
	});

	it("distinguishes an explicit pick from an automatic one", () => {
		renderBadges(facts({ kvCacheType: "f16", kvCacheTypeSource: "explicit", flashAttentionMode: "auto" }));

		expect(screen.getByText("KV f16 (explicit)")).toBeTruthy();
		expect(screen.getByText("FA auto")).toBeTruthy();
	});

	// A source the node never recorded is unknown, not explicit — labelling it "explicit" would invent provenance.
	it("omits the source suffix when the source was not recorded", () => {
		renderBadges(facts({ kvCacheType: "f16", kvCacheTypeSource: null }));

		expect(screen.getByText("KV f16")).toBeTruthy();
		expect(screen.queryByText("KV f16 (explicit)")).toBeNull();
	});

	// The reason is the only place the operator learns WHY Auto landed on this type, so the tooltip has to open. It
	// needs a host element that forwards a ref and the mouse handlers — the badge itself forwards neither.
	it("opens the auto-reason tooltip on hover", async () => {
		renderBadges(facts({ kvCacheType: "q8_0", kvCacheTypeSource: "auto", kvAutoReason: "manifest supports q8_0" }));

		fireEvent.mouseEnter(screen.getByTestId("benchmark-launch-kv").parentElement as HTMLElement);

		expect(await screen.findByText("manifest supports q8_0")).toBeTruthy();
	});

	it("reports a partial offload with its counts", () => {
		renderBadges(facts({ effectiveBackend: "cuda", placementOffloaded: 20, placementTotal: 32 }));

		expect(screen.getByText("CUDA 20/32 layers")).toBeTruthy();
	});

	it("names a fall back to CPU, an undetermined backend, and an unverified Metal launch distinctly", () => {
		const { unmount } = renderBadges(facts({ effectiveBackend: "cpu-fallback", placementOffloaded: 0, placementTotal: 32 }));
		expect(screen.getByText("CPU fallback")).toBeTruthy();
		unmount();

		const second = renderBadges(facts({ effectiveBackend: "unknown" }));
		expect(screen.getByText("backend unknown")).toBeTruthy();
		second.unmount();

		renderBadges(facts({ effectiveBackend: "metal-unverified" }));
		expect(screen.getByText("Metal (unverified)")).toBeTruthy();
	});

	it("flags an aux-asset launch and stays silent when none was attached", () => {
		const { unmount } = renderBadges(facts({ effectiveBackend: "cpu", hasAuxAssets: true }));
		expect(screen.getByText("adapter/aux asset")).toBeTruthy();
		unmount();

		renderBadges(facts({ effectiveBackend: "cpu", hasAuxAssets: false }));
		expect(screen.queryByText("adapter/aux asset")).toBeNull();
	});

	it("renders a dash for a run that predates the launch receipt", () => {
		renderBadges(noBenchmarkLaunchFacts);

		expect(screen.getByText("—")).toBeTruthy();
		expect(screen.queryByTestId("benchmark-launch-kv")).toBeNull();
	});
});
