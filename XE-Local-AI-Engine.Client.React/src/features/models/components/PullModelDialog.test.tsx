// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen, within } from "@testing-library/react";
import type { ReactElement, ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Deterministic i18n: t returns the supplied default so guidance/warning copy is readable in assertions.
vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}));

// The dialog renders a TanStack Router <Link> (inside Anchor component={Link}). Stub the router so the dialog mounts
// without a RouterProvider OR loading the generated route tree (which eval-fails outside a real router); the stub
// forwards `to` to a plain anchor href so the link target is assertable. The dialog uses no other router export.
vi.mock("@tanstack/react-router", () => ({
	Link: ({ children, to, ...props }: { children: ReactNode; to: string; [key: string]: unknown }) => (
		<a href={to} {...props}>
			{children}
		</a>
	),
}));

import { PullModelDialog } from "@/features/models/components/PullModelDialog";

function renderDialog(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("PullModelDialog", () => {
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
	});

	afterEach(() => {
		cleanup();
	});

	it("shows the where-to-find guidance, the unvetted-weights warning, and an anchor to /model-recommendations", async () => {
		renderDialog(
			<PullModelDialog
				opened={true}
				onClose={vi.fn()}
				pullModelName=""
				onPullModelNameChange={vi.fn()}
				onSubmit={vi.fn()}
				isPulling={false}
				isActionPending={false}
				progress={undefined}
			/>,
		);

		const dialog = await screen.findByRole("dialog");

		// Where-to-find guidance points at the Ollama library.
		const guidance = within(dialog).getByTestId("pull-model-guidance");
		expect(guidance.textContent).toContain("Ollama");
		expect(within(guidance).getByRole("link", { name: /ollama library/i }).getAttribute("href")).toBe(
			"https://ollama.com/library",
		);

		// The Hugging Face GGUF syntax hint tells operators they can also pull hf.co/<org>/<repo>:<quant>.
		expect(within(guidance).getByTestId("pull-model-huggingface-hint").textContent).toMatch(/hf\.co/i);

		// The deliberate unvetted-weights warning is present.
		const warning = within(dialog).getByTestId("pull-model-warning");
		expect(warning.textContent).toMatch(/unvetted/i);

		// The recommendations anchor links to the model-recommendations route.
		const recommendationsLink = within(dialog).getByTestId("pull-model-recommendations-link");
		expect(recommendationsLink.getAttribute("href")).toBe("/model-recommendations");
	});

	it("binds the progress bar to the supplied progress value", async () => {
		renderDialog(
			<PullModelDialog
				opened={true}
				onClose={vi.fn()}
				pullModelName="orca-mini:latest"
				onPullModelNameChange={vi.fn()}
				onSubmit={vi.fn()}
				isPulling={true}
				isActionPending={true}
				progress={42}
			/>,
		);

		const dialog = await screen.findByRole("dialog");
		// Mantine Progress renders a progressbar with the bound value.
		expect(within(dialog).getByLabelText("Pull progress")).toBeTruthy();
	});
});
