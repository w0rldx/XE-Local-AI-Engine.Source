// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { sourceThemeConfiguration } from "@/core/theme/config/ThemeConfiguration";
import { ThemeProvider } from "@/core/theme/provider/ThemeProvider";
import { ImageGenerationForm } from "@/features/images/components/ImageGenerationForm";
import type { ImageModelView } from "@/features/images/models/ImageModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The sampler/seed pair is the one row whose left half carries a *name*: split two-up at 768px the Select was too
// narrow for "Euler a" and rendered it as "Eule". It therefore goes two-up only from `lg`.
//
// This app overrides Mantine's breakpoints (src/theme/theme.json -> ThemeProvider, which divides the px values by 16),
// so `md` here is 768 — exactly the broken width — not Mantine's stock 62em. That is also why this must render inside
// the real ThemeProvider: under a bare MantineProvider the component emits stock breakpoints and the assertion would
// pass while the app stays broken. The expected queries are derived from the theme rather than hardcoded so the test
// follows theme.json if it ever moves.

const { md, lg } = sourceThemeConfiguration.breakpoints.values;
const mdQuery = `(min-width: ${md / 16}em)`;

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
	return renderWithProviders(
		<ThemeProvider>
			<ImageGenerationForm models={[model]} isSubmitting={false} onSubmit={vi.fn()} />
		</ThemeProvider>,
	);
}

// Mantine gives a responsive SimpleGrid a generated class and emits its column counts as CSS variables on that
// selector: the base rule plus one media block per declared breakpoint.
function responsiveRulesFor(element: HTMLElement): string {
	const generated = Array.from(element.classList).find((name) => name.startsWith("__m__"));
	// biome-ignore lint/suspicious/noMisplacedAssertion: guard inside a helper every caller runs from within a test — it fails the caller, not module load.
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

	it("keeps the sampler and the seed stacked in a single column by default", () => {
		renderForm();

		const rules = responsiveRulesFor(screen.getByTestId("image-form-sampler-row"));

		expect(rules).toContain("--sg-cols:1");
	});

	it("splits the row two-up only from the theme's lg breakpoint", () => {
		renderForm();

		const rules = responsiveRulesFor(screen.getByTestId("image-form-sampler-row"));

		expect(rules).toMatch(new RegExp(`\\(min-width: ${lg / 16}em\\)[^}]*\\{[^}]*--sg-cols:\\s*2`));
	});

	// The regression this pins: the theme's md IS 768px, the width at which the sampler was clipped. Two columns from
	// md upwards is the bug, not the fix.
	it("does not split the row at the theme's md breakpoint", () => {
		renderForm();

		const rules = responsiveRulesFor(screen.getByTestId("image-form-sampler-row"));

		expect(md).toBe(768);
		expect(rules).not.toContain(mdQuery);
	});
});
