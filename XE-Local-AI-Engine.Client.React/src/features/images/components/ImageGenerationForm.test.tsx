// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ImageGenerationForm } from "@/features/images/components/ImageGenerationForm";
import type { ImageModelView } from "@/features/images/models/ImageModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The sampler/seed pair is the one row whose left half carries a *name*: at ~768px a two-column split left the Select
// too narrow for "Euler a" and it rendered clipped. It therefore stacks below md (62em) rather than below sm (48em) —
// the assertions below read the media rules Mantine emits for the SimpleGrid so the breakpoint cannot drift back.

const model: ImageModelView = {
	modelName: "sd15",
	repoId: "runwayml/stable-diffusion-v1-5",
	family: "Sd15",
	kind: "Checkpoint",
	sizeBytes: 2_132_625_432,
	downloadedAtUtc: 0,
	defaultSteps: 20,
	defaultCfgScale: 7,
	defaultSampler: "euler_a",
};

function renderForm() {
	return renderWithProviders(<ImageGenerationForm models={[model]} isSubmitting={false} onSubmit={vi.fn()} />);
}

// Mantine gives a responsive SimpleGrid a generated class and emits its column counts as CSS variables on that
// selector: the base rule plus one media block per declared breakpoint.
function responsiveRulesFor(element: HTMLElement): string {
	const generated = Array.from(element.classList).find((name) => name.startsWith("__m__"));
	expect(generated).toBeTruthy();
	return Array.from(document.querySelectorAll("style"))
		.map((tag) => tag.textContent ?? "")
		.filter((css) => css.includes(`.${generated}`))
		.join("\n");
}

describe("ImageGenerationForm", () => {
	afterEach(cleanup);

	it("keeps the sampler and the seed in one responsive row", () => {
		renderForm();

		const row = screen.getByTestId("image-form-sampler-row");

		expect(row.contains(screen.getByTestId("image-form-sampler"))).toBe(true);
		expect(row.contains(screen.getByTestId("image-form-seed"))).toBe(true);
	});

	it("stacks the sampler and the seed below md so the sampler stays readable at a tablet width", () => {
		renderForm();

		const rules = responsiveRulesFor(screen.getByTestId("image-form-sampler-row"));

		expect(rules).toContain("--sg-cols:1");
		expect(rules).toContain("(min-width: 62em)");
		// 48em is the trap: two columns from `sm` upwards is exactly what clipped the sampler at 768px.
		expect(rules).not.toContain("(min-width: 48em)");
	});
});
