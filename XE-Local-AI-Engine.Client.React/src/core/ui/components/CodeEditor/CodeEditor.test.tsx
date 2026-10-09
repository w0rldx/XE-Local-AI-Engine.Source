// @vitest-environment jsdom

import { act, cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { CodeEditor } from "@/core/ui/components/CodeEditor/CodeEditor";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

const editorMock = vi.hoisted(() => {
	let value = "";
	let contentListener: (() => void) | undefined;
	let hasFocus = false;
	const instance = {
		getValue: vi.fn(() => value),
		hasTextFocus: vi.fn(() => hasFocus),
		setValue: vi.fn((next: string) => {
			value = next;
			// Monaco emits onDidChangeModelContent for programmatic writes too.
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
		setModelLanguage: vi.fn(),
		setTheme: vi.fn(),
		/** Simulates the user typing: mutates the model then fires the change listener, exactly as Monaco does. */
		type(next: string) {
			value = next;
			contentListener?.();
		},
		focus(next: boolean) {
			hasFocus = next;
		},
	};
});

// The real module boots the ~3 MB Monaco runtime and needs a layout engine jsdom does not have; the wrapper's contract
// (create once, apply props in place, forward edits) is what this file pins.
vi.mock("@/core/ui/components/CodeEditor/MonacoRuntime", () => ({
	monaco: {
		editor: { create: editorMock.create, setModelLanguage: editorMock.setModelLanguage, setTheme: editorMock.setTheme },
	},
}));

// `render(ui, { wrapper })` rather than `renderWithProviders(ui)`: several tests below assert that a re-render applies
// props IN PLACE instead of recreating the editor, and only the wrapper form lets `rerender` reuse the same provider
// tree. Passing the providers inline to `rerender` would remount the subtree and destroy exactly what is under test.
function renderEditor(ui: ReactElement) {
	const { wrapper } = createProvidersWrapper();
	return render(ui, { wrapper });
}

describe("CodeEditor", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		editorMock.focus(false);
	});
	afterEach(() => cleanup());

	it("lazily mounts Monaco with the given value, language and read-only state", async () => {
		renderEditor(<CodeEditor value="+added" language="diff" readOnly={true} data-testid="viewer" aria-label="Patch" />);

		expect(await screen.findByTestId("viewer")).toBeTruthy();
		expect(editorMock.create).toHaveBeenCalledTimes(1);
		expect(editorMock.create.mock.calls[0]?.[1]).toMatchObject({
			value: "+added",
			language: "diff",
			readOnly: true,
			domReadOnly: true,
			ariaLabel: "Patch",
		});
		expect(editorMock.setTheme).toHaveBeenCalledWith("xe-light");
	});

	it("applies value and language changes in place without recreating the editor", async () => {
		const { rerender } = renderEditor(<CodeEditor value="a" language="json" data-testid="viewer" />);
		await screen.findByTestId("viewer");

		rerender(<CodeEditor value="b" language="yaml" data-testid="viewer" />);

		expect(editorMock.create).toHaveBeenCalledTimes(1);
		expect(editorMock.instance.setValue).toHaveBeenCalledWith("b");
		expect(editorMock.setModelLanguage).toHaveBeenLastCalledWith(expect.anything(), "yaml");
	});

	it("forwards edits through onChange and does not echo the same value back into the model", async () => {
		const onChange = vi.fn();
		const { rerender } = renderEditor(<CodeEditor value="a" onChange={onChange} data-testid="editor" />);
		await screen.findByTestId("editor");

		act(() => editorMock.type("ab"));
		expect(onChange).toHaveBeenCalledWith("ab");

		// The controlled round-trip: parent stores "ab" and re-renders with it. Monaco already holds "ab", so
		// setValue must NOT run (it would reset the caret and undo stack).
		editorMock.instance.setValue.mockClear();
		rerender(<CodeEditor value="ab" onChange={onChange} data-testid="editor" />);
		expect(editorMock.instance.setValue).not.toHaveBeenCalled();
	});

	it("does not report a prop-driven value replacement as a user edit", async () => {
		const onChange = vi.fn();
		const { rerender } = renderEditor(<CodeEditor value="a" onChange={onChange} data-testid="editor" />);
		await screen.findByTestId("editor");

		rerender(<CodeEditor value="replaced by parent" onChange={onChange} data-testid="editor" />);

		expect(editorMock.instance.setValue).toHaveBeenCalledWith("replaced by parent");
		expect(onChange).not.toHaveBeenCalled();

		// A real edit afterwards still reaches the parent.
		act(() => editorMock.type("replaced by parent!"));
		expect(onChange).toHaveBeenCalledWith("replaced by parent!");
	});

	it("disposes the editor on unmount", async () => {
		const { unmount } = renderEditor(<CodeEditor value="a" data-testid="viewer" />);
		await screen.findByTestId("viewer");
		unmount();
		expect(editorMock.instance.dispose).toHaveBeenCalledTimes(1);
	});

	it("does not rewind the model to a lagging `value` while the operator is typing", async () => {
		// The controlled round trip is asynchronous: the parent re-renders one keystroke behind. Writing that echo back
		// reset the model to the stale text and dropped everything typed since, which reads as lost and reordered input.
		const { rerender } = renderEditor(<CodeEditor value="ab" data-testid="editor" onChange={vi.fn()} />);
		await screen.findByTestId("editor");
		editorMock.focus(true);

		act(() => editorMock.type("abcd"));
		// The parent has only caught up as far as "abc".
		rerender(<CodeEditor value="abc" data-testid="editor" onChange={vi.fn()} />);

		expect(editorMock.instance.setValue).not.toHaveBeenCalled();
		expect(editorMock.instance.getValue()).toBe("abcd");
	});

	it("still applies a value the parent changed while the editor is not focused", async () => {
		const { rerender } = renderEditor(<CodeEditor value="ab" data-testid="editor" onChange={vi.fn()} />);
		await screen.findByTestId("editor");

		rerender(<CodeEditor value="replaced" data-testid="editor" onChange={vi.fn()} />);

		expect(editorMock.instance.setValue).toHaveBeenCalledWith("replaced");
	});

	it("inspect mode forces read-only and renders whitespace, control characters and invisible or ambiguous Unicode", async () => {
		const withZeroWidthSpace = "a\u200Bb";
		renderEditor(<CodeEditor value={withZeroWidthSpace} inspect={true} readOnly={false} data-testid="viewer" />);
		await screen.findByTestId("viewer");

		expect(editorMock.create.mock.calls[0]?.[1]).toMatchObject({
			readOnly: true,
			domReadOnly: true,
			renderWhitespace: "all",
			renderControlCharacters: true,
			unicodeHighlight: {
				invisibleCharacters: true,
				ambiguousCharacters: true,
				nonBasicASCII: false,
				includeComments: true,
				includeStrings: true,
				// No locale exemption: a look-alike is flagged whatever the reviewer's own language.
				allowedLocales: {},
			},
		});
	});

	it("inspect mode fails closed to an alert, never the plain-text fallback, when the editor chunk cannot load", async () => {
		// A fresh module graph whose lazy editor import rejects, as an offline chunk miss does. The plain <Code> fallback
		// would show the untrusted text with its zero-width characters invisible again, so it must not appear.
		vi.resetModules();
		vi.doMock("@/core/ui/components/CodeEditor/MonacoCodeEditor", () => {
			throw new Error("chunk load failed");
		});
		vi.spyOn(console, "error").mockImplementation(() => undefined);
		const { CodeEditor: FailingEditor } = await import("@/core/ui/components/CodeEditor/CodeEditor");

		const untrusted = "hidden\u200Btext";
		renderEditor(<FailingEditor value={untrusted} inspect={true} data-testid="viewer" />);

		const alert = await screen.findByRole("alert");
		expect(alert.textContent).toContain("The inspection editor could not load");
		expect(screen.queryByText(/hidden/)).toBeNull();
		vi.doUnmock("@/core/ui/components/CodeEditor/MonacoCodeEditor");
	});
});
