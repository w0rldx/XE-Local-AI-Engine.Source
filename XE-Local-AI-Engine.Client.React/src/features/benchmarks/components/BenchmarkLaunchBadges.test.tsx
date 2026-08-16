// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
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

		expect(screen.getByText("KV f16")).toBeTruthy();
		expect(screen.getByText("FA auto")).toBeTruthy();
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
