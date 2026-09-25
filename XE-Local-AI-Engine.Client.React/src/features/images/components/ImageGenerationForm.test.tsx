// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { sourceThemeConfiguration } from "@/core/theme/config/ThemeConfiguration";
import { ThemeProvider } from "@/core/theme/provider/ThemeProvider";
import { ImageGenerationForm } from "@/features/images/components/ImageGenerationForm";
import { imageFormOverridesKeyPrefix } from "@/features/images/models/ImageFormOverrides";
import type { ImageModelView } from "@/features/images/models/ImageModels";
import en from "@/locales/en.json";
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

// The family default for Qwen-Image 2.1 is CFG 6.0; a manually imported original wants 2.5. The operator's edit must
// survive a model switch and a reload, and be revertible.
const qwen: ImageModelView = {
	...model,
	modelName: "qwen-image-original",
	repoId: "Qwen/Qwen-Image",
	family: "QwenImage",
	defaultSteps: 40,
	defaultCfgScale: 6,
	defaultSampler: "euler",
};

const resetLabel = en.pages.images.form.resetSampling;

function renderBoth() {
	return renderWithProviders(
		<ThemeProvider>
			<ImageGenerationForm models={[qwen, model]} isSubmitting={false} onSubmit={vi.fn()} />
		</ThemeProvider>,
	);
}

function cfgInput(): HTMLInputElement {
	return screen.getByTestId("image-form-cfg-scale") as HTMLInputElement;
}

async function pickModel(name: string) {
	fireEvent.click(screen.getByTestId("image-form-model"));
	fireEvent.click(await screen.findByRole("option", { name, hidden: true }));
}

describe("ImageGenerationForm per-model overrides", () => {
	beforeEach(() => localStorage.clear());
	afterEach(cleanup);

	it("restores the edited CFG after switching to another model and back", async () => {
		renderBoth();

		fireEvent.change(cfgInput(), { target: { value: "2.5" } });
		await pickModel("sd15");
		expect(cfgInput().value).toBe("7");
		await pickModel("qwen-image-original");

		expect(cfgInput().value).toBe("2.5");
	});

	it("seeds a fresh mount from the stored override", () => {
		localStorage.setItem(
			`${imageFormOverridesKeyPrefix}qwen-image-original`,
			JSON.stringify({ steps: 30, cfgScale: 2.5, sampler: "euler" }),
		);

		renderBoth();

		expect(cfgInput().value).toBe("2.5");
		expect((screen.getByTestId("image-form-steps") as HTMLInputElement).value).toBe("30");
		expect(screen.getByRole("button", { name: resetLabel })).toBeTruthy();
	});

	it("reset clears the override, restores the family CFG and hides itself", () => {
		renderBoth();
		expect(screen.queryByRole("button", { name: resetLabel })).toBeNull();
		fireEvent.change(cfgInput(), { target: { value: "2.5" } });

		fireEvent.click(screen.getByRole("button", { name: resetLabel }));

		expect(cfgInput().value).toBe("6");
		expect(localStorage.getItem(`${imageFormOverridesKeyPrefix}qwen-image-original`)).toBeNull();
		expect(screen.queryByRole("button", { name: resetLabel })).toBeNull();
	});

	it("ignores an invalid stored override and shows the family default", () => {
		localStorage.setItem(
			`${imageFormOverridesKeyPrefix}qwen-image-original`,
			JSON.stringify({ steps: 30, cfgScale: 99, sampler: "euler" }),
		);

		renderBoth();

		expect(cfgInput().value).toBe("6");
		expect(screen.queryByRole("button", { name: resetLabel })).toBeNull();
	});
});
