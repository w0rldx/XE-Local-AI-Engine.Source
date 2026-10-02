// @vitest-environment jsdom

import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { sourceThemeConfiguration } from "@/core/theme/config/ThemeConfiguration";
import { ThemeProvider } from "@/core/theme/provider/ThemeProvider";
import { ImageGenerationForm } from "@/features/images/components/ImageGenerationForm";
import { imageFormOverridesKeyPrefix } from "@/features/images/models/ImageFormOverrides";
import type { ImageGenerationFormValues, ImageModelView } from "@/features/images/models/ImageModels";
import en from "@/locales/en.json";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper, renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// The form embeds the prompt assist, which reads the installed chat models and the running set on mount.
setupMswServer(
	jsonRoute("get", "models", {
		isAvailable: true,
		items: [
			{
				modelName: "qwen3-4b",
				provider: "llamacpp",
				kind: "Chat",
				detectedKind: "Chat",
				capabilities: [],
				isSelected: true,
				isReasoningCapable: false,
				isToolCapable: false,
				isOverridden: false,
			},
		],
	}),
	jsonRoute("get", "models/running", { isAvailable: true, ollamaConfigured: false, items: [] }),
	jsonRoute("get", "model-fit/running", { items: [] }),
);

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
	editModes: ["img2img"],
	nativePixels: 512 * 512,
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

describe("ImageGenerationForm prompt assist", () => {
	beforeEach(() => localStorage.clear());
	afterEach(cleanup);

	it("fills the prompt and negative prompt from an applied AI draft", async () => {
		let sent: Record<string, unknown> | undefined;
		server.use(
			http.post(localApiPath("images/prompts/draft"), async ({ request }) => {
				sent = (await request.json()) as Record<string, unknown>;
				return HttpResponse.json({
					prompt: "A red fox in a misty birch forest at dawn, soft volumetric light, watercolor",
					negativePrompt: "blurry, watermark",
					generationMetadata: { model: "qwen3-4b", mode: "Create", assumptions: [], confidence: 0.7 },
				});
			}),
		);
		renderForm();

		const open = await screen.findByTestId("assist-open-create");
		await waitFor(() => expect(open).toHaveProperty("disabled", false));
		expect(open.textContent).toBe(en.assist.promptButton);
		fireEvent.click(open);
		fireEvent.change(await screen.findByTestId("assist-brief"), { target: { value: "a fox in a forest" } });
		fireEvent.click(screen.getByTestId("assist-generate"));
		await screen.findByTestId("assist-result");
		fireEvent.click(screen.getByTestId("assist-apply"));

		await waitFor(() =>
			expect((screen.getByTestId("image-form-prompt") as HTMLTextAreaElement).value).toBe(
				"A red fox in a misty birch forest at dawn, soft volumetric light, watercolor",
			),
		);
		expect((screen.getByTestId("image-form-negative-prompt") as HTMLTextAreaElement).value).toBe("blurry, watermark");
		expect(sent).toMatchObject({ mode: "Create", modelName: "qwen3-4b", brief: "a fox in a forest" });
	});
});

// Edit mode: the same form, prefilled from the source image. The defaults matter more than they look — a phone photo's
// own 4000×3000 would be an out-of-memory job, so the size starts at the source's aspect fitted to the model's native
// pixel count, and the mode list is the model's own.
describe("ImageGenerationForm edit mode", () => {
	const source = { imageId: "99999999-9999-4999-8999-999999999999", width: 4000, height: 3000 };
	const qwenEdit: ImageModelView = {
		...model,
		modelName: "qwen-image-edit",
		family: "QwenImage",
		editModes: ["img2img", "reference"],
		nativePixels: 1024 * 1024,
	};
	const noEdit: ImageModelView = { ...model, modelName: "no-edit", editModes: [] };

	beforeEach(() => {
		localStorage.clear();
		Object.assign(URL, { createObjectURL: () => "blob:source", revokeObjectURL: () => undefined });
		server.use(
			http.get(
				localApiPath(`images/${source.imageId}`),
				() => new HttpResponse(new Blob(["png"]), { headers: { "content-type": "image/png" } }),
			),
		);
	});
	afterEach(cleanup);

	function renderEdit(models: readonly ImageModelView[], onSubmit = vi.fn(), onCancelEdit = vi.fn()) {
		renderWithProviders(
			<ThemeProvider>
				<ImageGenerationForm
					models={models}
					isSubmitting={false}
					onSubmit={onSubmit}
					editSource={source}
					onCancelEdit={onCancelEdit}
				/>
			</ThemeProvider>,
		);
		return { onSubmit, onCancelEdit };
	}

	function numberValue(testId: string): string {
		return (screen.getByTestId(testId) as HTMLInputElement).value;
	}

	function submitWithPrompt(prompt: string) {
		fireEvent.change(screen.getByTestId("image-form-prompt"), { target: { value: prompt } });
		fireEvent.click(screen.getByTestId("image-form-submit"));
	}

	it("shows the source and fits its aspect to the model's native pixels", async () => {
		renderEdit([model]);

		expect(screen.getByText(en.pages.images.edit.editingFrom)).toBeTruthy();
		expect((await screen.findByTestId("image-source-thumbnail")).getAttribute("src")).toBe("blob:source");
		expect(numberValue("image-form-width")).toBe("576");
		expect(numberValue("image-form-height")).toBe("448");
	});

	it("hides the mode selector for a model with one mode and shows the strength slider for img2img", () => {
		renderEdit([model]);

		expect(screen.queryByTestId("image-form-edit-mode")).toBeNull();
		expect(screen.getByText(en.pages.images.edit.strength.label)).toBeTruthy();
		expect(screen.getByRole("slider").getAttribute("aria-valuenow")).toBe("0.75");
	});

	it("submits img2img with the source and strength", () => {
		const { onSubmit } = renderEdit([model]);

		submitWithPrompt("make it autumn");

		expect(onSubmit).toHaveBeenCalledWith(
			expect.objectContaining({
				prompt: "make it autumn",
				editMode: "img2img",
				sourceImageId: source.imageId,
				strength: 0.75,
				width: 576,
				height: 448,
			}),
		);
	});

	it("offers the model's modes, and reference drops the strength and asks for the change", () => {
		const { onSubmit } = renderEdit([qwenEdit]);
		expect(screen.getByTestId("image-form-edit-mode")).toBeTruthy();

		fireEvent.click(screen.getByRole("radio", { name: en.pages.images.edit.modes.reference }));

		expect(screen.queryByRole("slider")).toBeNull();
		expect(screen.getByTestId("image-form-prompt").getAttribute("placeholder")).toBe(en.pages.images.edit.referencePlaceholder);
		submitWithPrompt("replace the sky with a sunset");
		const sent = onSubmit.mock.calls[0]?.[0] as ImageGenerationFormValues;
		expect(sent).toMatchObject({ editMode: "reference", sourceImageId: source.imageId, width: 1152, height: 896 });
		expect(sent.strength).toBeUndefined();
	});

	it("re-derives the size and drops a mode the newly picked model lacks", async () => {
		renderEdit([qwenEdit, model]);
		fireEvent.click(screen.getByRole("radio", { name: en.pages.images.edit.modes.reference }));
		expect(numberValue("image-form-width")).toBe("1152");

		await pickModel("sd15");

		expect(numberValue("image-form-width")).toBe("576");
		expect(numberValue("image-form-height")).toBe("448");
		expect(screen.queryByTestId("image-form-edit-mode")).toBeNull();
		expect(screen.getByRole("slider")).toBeTruthy();
	});

	it("blocks submit on a model that cannot edit", () => {
		renderEdit([noEdit]);

		expect(screen.getByTestId("image-form-edit-unsupported").textContent).toBe(en.pages.images.edit.unsupported);
		expect(screen.getByTestId("image-form-submit")).toHaveProperty("disabled", true);
	});

	it("leaves edit mode through Cancel edit", () => {
		const { onCancelEdit } = renderEdit([model]);

		fireEvent.click(screen.getByRole("button", { name: en.pages.images.edit.cancel }));

		expect(onCancelEdit).toHaveBeenCalledTimes(1);
	});

	it("sends no edit fields once the page clears the source", () => {
		const onSubmit = vi.fn();
		const { wrapper } = createProvidersWrapper();
		const { rerender } = render(
			<ThemeProvider>
				<ImageGenerationForm models={[model]} isSubmitting={false} onSubmit={onSubmit} editSource={source} />
			</ThemeProvider>,
			{ wrapper },
		);
		rerender(
			<ThemeProvider>
				<ImageGenerationForm models={[model]} isSubmitting={false} onSubmit={onSubmit} editSource={null} />
			</ThemeProvider>,
		);

		expect(screen.queryByTestId("image-form-edit")).toBeNull();
		submitWithPrompt("a fox");
		const sent = onSubmit.mock.calls[0]?.[0] as ImageGenerationFormValues;
		expect(sent.editMode).toBeUndefined();
		expect(sent.sourceImageId).toBeUndefined();
		expect(sent.strength).toBeUndefined();
	});
});
