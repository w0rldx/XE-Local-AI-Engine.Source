// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { MarkdownEditorField } from "@/core/ui/components/MarkdownEditorField/MarkdownEditorField";
import { testMantineTheme } from "@/test/MantineTestRender";

// A fake Monaco at the runtime level: the real CodeEditor and MonacoCodeEditor run, only the ~3 MB editor is faked.
const editorMock = vi.hoisted(() => {
	let value = "";
	let contentListener: (() => void) | undefined;
	const instance = {
		getValue: vi.fn(() => value),
		hasTextFocus: vi.fn(() => false),
		setValue: vi.fn((next: string) => {
			value = next;
			contentListener?.();
		}),
		getModel: vi.fn(() => ({ dispose: vi.fn() })),
		updateOptions: vi.fn(),
		onDidChangeModelContent: vi.fn((listener: () => void) => {
			contentListener = listener;
			return { dispose: vi.fn() };
		}),
		dispose: vi.fn(),
	};
	return {
		instance,
		create: vi.fn((_container: HTMLElement, options: { value: string }) => {
			value = options.value;
			return instance;
		}),
		/** Simulates the user typing: mutates the model, then fires the change listener as Monaco does. */
		type(next: string) {
			value = next;
			contentListener?.();
		},
	};
});

vi.mock("@/core/ui/components/CodeEditor/MonacoRuntime", () => ({
	monaco: { editor: { create: editorMock.create, setModelLanguage: vi.fn(), setTheme: vi.fn() } },
}));

function renderWithProviders(ui: ReactElement) {
	return render(
		<MantineProvider env="test" theme={testMantineTheme}>
			{ui}
		</MantineProvider>,
	);
}

describe("MarkdownEditorField", () => {
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
		// jsdom lacks FontFaceSet API that Mantine's autosize Textarea subscribes to.
		Object.defineProperty(document, "fonts", {
			configurable: true,
			value: { addEventListener: vi.fn(), removeEventListener: vi.fn() },
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("renders a textarea in edit mode by default", () => {
		renderWithProviders(<MarkdownEditorField value="hello" onChange={vi.fn()} data-testid="mef" />);
		expect(screen.getByTestId("mef-textarea")).toBeTruthy();
	});

	it("calls onChange when the textarea value changes", () => {
		const onChange = vi.fn();
		renderWithProviders(<MarkdownEditorField value="" onChange={onChange} data-testid="mef" />);
		fireEvent.change(screen.getByTestId("mef-textarea"), { target: { value: "new text" } });
		expect(onChange).toHaveBeenCalledWith("new text");
	});

	it("switches to preview pane when Preview segment is clicked", () => {
		renderWithProviders(<MarkdownEditorField value="**bold**" onChange={vi.fn()} data-testid="mef" />);
		fireEvent.click(screen.getByText("Preview"));
		expect(screen.getByTestId("mef-preview")).toBeTruthy();
		// Textarea should no longer be in the DOM
		expect(screen.queryByTestId("mef-textarea")).toBeNull();
	});

	it("renders markdown content in preview mode", () => {
		renderWithProviders(<MarkdownEditorField value="Hello **world**" onChange={vi.fn()} data-testid="mef" />);
		fireEvent.click(screen.getByText("Preview"));
		// The preview pane should contain the rendered text
		expect(screen.getByTestId("mef-preview").textContent).toContain("Hello");
		expect(screen.getByTestId("mef-preview").textContent).toContain("world");
	});

	it("switches back to edit mode when Edit segment is clicked", () => {
		renderWithProviders(<MarkdownEditorField value="text" onChange={vi.fn()} data-testid="mef" />);
		fireEvent.click(screen.getByText("Preview"));
		fireEvent.click(screen.getByText("Edit"));
		expect(screen.getByTestId("mef-textarea")).toBeTruthy();
		expect(screen.queryByTestId("mef-preview")).toBeNull();
	});

	it("renders label and required marker in preview mode", () => {
		renderWithProviders(
			<MarkdownEditorField value="" onChange={vi.fn()} label="Instructions" required={true} data-testid="mef" />,
		);
		fireEvent.click(screen.getByText("Preview"));
		expect(screen.getByText("Instructions")).toBeTruthy();
		expect(screen.getByText("*")).toBeTruthy();
	});

	it("renders an error message in preview mode", () => {
		renderWithProviders(<MarkdownEditorField value="" onChange={vi.fn()} error="Required field" data-testid="mef" />);
		fireEvent.click(screen.getByText("Preview"));
		expect(screen.getByText("Required field")).toBeTruthy();
	});

	describe("code editor variant", () => {
		beforeEach(() => {
			vi.clearAllMocks();
		});

		it("renders a markdown code editor inside the label, description and error wrapper, named by the label", async () => {
			renderWithProviders(
				<MarkdownEditorField
					value="# Title"
					onChange={vi.fn()}
					editor="code"
					label="Body"
					description="The full instructions."
					error="Body is required."
					required={true}
					data-testid="mef"
				/>,
			);

			expect(await screen.findByTestId("mef-editor")).toBeTruthy();
			expect(screen.queryByTestId("mef-textarea")).toBeNull();
			expect(screen.getByText("Body")).toBeTruthy();
			expect(screen.getByText("*")).toBeTruthy();
			expect(screen.getByText("The full instructions.")).toBeTruthy();
			expect(screen.getByText("Body is required.")).toBeTruthy();
			expect(editorMock.create.mock.calls[0]?.[1]).toMatchObject({
				value: "# Title",
				language: "markdown",
				wordWrap: "on",
				readOnly: false,
				ariaLabel: "Body",
			});
		});

		it("forwards an edit in the code editor through onChange", async () => {
			const onChange = vi.fn();
			renderWithProviders(<MarkdownEditorField value="" onChange={onChange} editor="code" data-testid="mef" />);
			await screen.findByTestId("mef-editor");

			act(() => editorMock.type("typed text"));

			expect(onChange).toHaveBeenCalledWith("typed text");
		});

		it("toggles between the code editor and the preview", async () => {
			renderWithProviders(<MarkdownEditorField value="Hello **world**" onChange={vi.fn()} editor="code" data-testid="mef" />);
			await screen.findByTestId("mef-editor");

			fireEvent.click(screen.getByText("Preview"));
			expect(screen.getByTestId("mef-preview").textContent).toContain("world");
			expect(screen.queryByTestId("mef-editor")).toBeNull();
			expect(editorMock.instance.dispose).toHaveBeenCalledTimes(1);

			fireEvent.click(screen.getByText("Edit"));
			expect(await screen.findByTestId("mef-editor")).toBeTruthy();
			expect(screen.queryByTestId("mef-preview")).toBeNull();
		});
	});
});
