// @vitest-environment jsdom

import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { CodeDiffViewer } from "@/core/ui/components/CodeEditor/CodeDiffViewer";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

const diffMock = vi.hoisted(() => {
	const makeModel = (initial: string) => {
		let value = initial;
		return {
			getValue: vi.fn(() => value),
			setValue: vi.fn((next: string) => {
				value = next;
			}),
			dispose: vi.fn(),
		};
	};
	type FakeModel = ReturnType<typeof makeModel>;
	const models: FakeModel[] = [];
	const viewModel = { dispose: vi.fn() };
	const editor = {
		createViewModel: vi.fn((_model: { original: FakeModel; modified: FakeModel }) => viewModel),
		setModel: vi.fn(),
		getModel: vi.fn(),
		updateOptions: vi.fn(),
		dispose: vi.fn(),
	};
	return {
		editor,
		viewModel,
		models,
		createDiffEditor: vi.fn((_container: HTMLElement, _options: Record<string, unknown>) => editor),
		createModel: vi.fn((value: string) => {
			const model = makeModel(value);
			models.push(model);
			return model;
		}),
		setModelLanguage: vi.fn(),
		setTheme: vi.fn(),
	};
});

// The real module boots the Monaco runtime, which jsdom cannot lay out; the wrapper's lifecycle contract is what is pinned.
vi.mock("@/core/ui/components/CodeEditor/MonacoRuntime", () => ({
	monaco: {
		editor: {
			createDiffEditor: diffMock.createDiffEditor,
			createModel: diffMock.createModel,
			setModelLanguage: diffMock.setModelLanguage,
			setTheme: diffMock.setTheme,
		},
	},
}));

/** Installs the provider stubs, then answers the `min-width` breakpoint query with `wide`. */
function renderViewer(ui: ReactElement, wide: boolean) {
	const { wrapper } = createProvidersWrapper();
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: query.includes("min-width") ? wide : false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			addListener: vi.fn(),
			removeListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
	return render(ui, { wrapper });
}

describe("CodeDiffViewer", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		diffMock.models.length = 0;
	});
	afterEach(() => cleanup());

	it("creates one read-only side-by-side diff editor on a wide screen and loads both texts", async () => {
		renderViewer(<CodeDiffViewer original="old" modified="new" language="markdown" data-testid="diff" />, true);
		await screen.findByTestId("diff");

		expect(diffMock.createDiffEditor).toHaveBeenCalledTimes(1);
		expect(diffMock.createDiffEditor.mock.calls[0]?.[1]).toMatchObject({
			readOnly: true,
			originalEditable: false,
			renderSideBySide: true,
			// Trailing spaces and indentation are Markdown syntax; the comparison must not hide them.
			ignoreTrimWhitespace: false,
			wordWrap: "on",
		});
		expect(diffMock.createModel).toHaveBeenNthCalledWith(1, "old", "markdown");
		expect(diffMock.createModel).toHaveBeenNthCalledWith(2, "new", "markdown");
		expect(diffMock.editor.createViewModel).toHaveBeenCalledWith({
			original: diffMock.models[0],
			modified: diffMock.models[1],
		});
		expect(diffMock.editor.setModel).toHaveBeenCalledWith(diffMock.viewModel);
		expect(diffMock.setTheme).toHaveBeenCalledWith("xe-light");
	});

	it("renders inline below the md breakpoint", async () => {
		renderViewer(<CodeDiffViewer original="old" modified="new" data-testid="diff" />, false);
		await screen.findByTestId("diff");

		expect(diffMock.createDiffEditor.mock.calls[0]?.[1]).toMatchObject({ readOnly: true, renderSideBySide: false });
	});

	it("writes changed texts into the existing models instead of recreating the editor", async () => {
		const { rerender } = renderViewer(<CodeDiffViewer original="old" modified="new" data-testid="diff" />, true);
		await screen.findByTestId("diff");

		rerender(<CodeDiffViewer original="old" modified="newer" data-testid="diff" />);

		expect(diffMock.createDiffEditor).toHaveBeenCalledTimes(1);
		expect(diffMock.createModel).toHaveBeenCalledTimes(2);
		expect(diffMock.models[1]?.setValue).toHaveBeenCalledWith("newer");
		expect(diffMock.models[0]?.setValue).not.toHaveBeenCalled();
	});

	it("disposes the diff editor, then the view model it owns, then both models on unmount", async () => {
		const { unmount } = renderViewer(<CodeDiffViewer original="old" modified="new" data-testid="diff" />, true);
		await screen.findByTestId("diff");

		unmount();

		expect(diffMock.editor.dispose).toHaveBeenCalledTimes(1);
		// The view model cancels the in-flight diff computation; disposing the models first would make it reject.
		expect(diffMock.viewModel.dispose).toHaveBeenCalledTimes(1);
		expect(diffMock.models[0]?.dispose).toHaveBeenCalledTimes(1);
		expect(diffMock.models[1]?.dispose).toHaveBeenCalledTimes(1);
		const order = [
			diffMock.editor.dispose.mock.invocationCallOrder[0],
			diffMock.viewModel.dispose.mock.invocationCallOrder[0],
			diffMock.models[0]?.dispose.mock.invocationCallOrder[0],
			diffMock.models[1]?.dispose.mock.invocationCallOrder[0],
		];
		expect(order).toEqual([...order].sort((a, b) => (a ?? 0) - (b ?? 0)));
	});

	it("shows an alert instead of the comparison when the editor cannot be created", async () => {
		diffMock.createDiffEditor.mockImplementationOnce(() => {
			throw new Error("editor failed");
		});
		vi.spyOn(console, "error").mockImplementation(() => undefined);

		renderViewer(<CodeDiffViewer original="old" modified="new" data-testid="diff" />, true);

		const alert = await screen.findByRole("alert");
		expect(alert.textContent).toContain("The comparison could not load.");
	});
});
