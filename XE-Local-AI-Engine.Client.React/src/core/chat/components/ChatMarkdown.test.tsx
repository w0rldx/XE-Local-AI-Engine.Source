// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ChatMarkdown } from "@/core/chat/components/ChatMarkdown";
import { normalizeMath } from "@/core/chat/components/ChatMath";
import { testMantineTheme } from "@/test/MantineTestRender";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

function renderMarkdown(content: string) {
	return render(
		<MantineProvider env="test" theme={testMantineTheme}>
			<ChatMarkdown content={content} />
		</MantineProvider>,
	).container;
}

beforeEach(() => {
	// Mantine's color-scheme provider reads matchMedia/ResizeObserver on mount; jsdom ships neither.
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

describe("normalizeMath", () => {
	it("rewrites \\( \\) to text math and \\[ \\] to a display block", () => {
		expect(normalizeMath("so \\(a+b\\) holds")).toBe("so $$a+b$$ holds");
		expect(normalizeMath("delimiters \\( \\) and \\[ \\]")).toBe("delimiters \\( \\) and \\[ \\]");
		expect(normalizeMath("\\[ x^2 \\]")).toBe("\n$$\nx^2\n$$\n");
	});

	it("rewrites a Pandoc-style single-dollar span and keeps prices as text", () => {
		expect(normalizeMath("area $\\pi r^2$ here")).toBe("area $$\\pi r^2$$ here");
		expect(normalizeMath("It costs $5 and $10, or $5-$10.")).toBe("It costs $5 and $10, or $5-$10.");
		expect(normalizeMath("escaped \\$x$ stays")).toBe("escaped \\$x$ stays");
	});

	it("leaves fenced code, unclosed fences and inline code untouched", () => {
		const fenced = "```tex\n\\(x\\) $y$\n```";
		expect(normalizeMath(fenced)).toBe(fenced);
		const streaming = "```sh\necho $A$B \\[";
		expect(normalizeMath(streaming)).toBe(streaming);
		expect(normalizeMath("run `echo $x$` now")).toBe("run `echo $x$` now");
	});

	it("leaves inline code with a longer backtick run untouched", () => {
		expect(normalizeMath("run ``a $x$ b`` now")).toBe("run ``a $x$ b`` now");
		expect(normalizeMath("run ``a ` $x$ b`` now")).toBe("run ``a ` $x$ b`` now");
	});

	it("keeps \\[ \\] inside a line as text math, and an escaped bracket that holds no math as written", () => {
		expect(normalizeMath("| a | \\[x^2\\] |")).toBe("| a | $$x^2$$ |");
		expect(normalizeMath("see \\[1\\] and \\[Note\\]")).toBe("see \\[1\\] and \\[Note\\]");
		expect(normalizeMath("\\[1\\]")).toBe("\\[1\\]");
	});

	it("closes a fence only on a line-start run of its own character at least as long as the opener", () => {
		const longerFence = "````md\n```\n$a$\n```\n````";
		expect(normalizeMath(longerFence)).toBe(longerFence);
		const quotedFence = "```bash\necho '```'\necho $A:$B\n```";
		expect(normalizeMath(quotedFence)).toBe(quotedFence);
		const midLineFence = "```markdown\nOpen a fence with ``` and write $x$ for math.\n```";
		expect(normalizeMath(midLineFence)).toBe(midLineFence);
		const quotedBlock = "> ```\n> $y$\n> ```\n\nthen $z$";
		expect(normalizeMath(quotedBlock)).toBe("> ```\n> $y$\n> ```\n\nthen $$z$$");
	});

	it("leaves inline code that wraps onto the next line untouched, but not past a blank line", () => {
		expect(normalizeMath("`foo\nbar $x$ baz`")).toBe("`foo\nbar $x$ baz`");
		expect(normalizeMath("`open\n\nand $x$ `")).toBe("`open\n\nand $$x$$ `");
	});

	it("keeps a multi-line \\( \\) on one line so it never opens a display block", () => {
		expect(normalizeMath("\\(a +\nb\\) is the sum.")).toBe("$$a + b$$ is the sum.");
	});

	it("keeps a $$ $$ line inside its list item", () => {
		expect(normalizeMath("1. First:\n   $$a^2$$\n2. Second")).toBe("1. First:\n\n   $$\n   a^2\n   $$\n\n2. Second");
	});

	it("leaves dollars in a link destination alone", () => {
		expect(normalizeMath("[a](http://x/?a=$b&c=$d)")).toBe("[a](http://x/?a=$b&c=$d)");
	});

	it("leaves a bare URL alone", () => {
		expect(normalizeMath("see https://x.io/?a=$b$c")).toBe("see https://x.io/?a=$b$c");
	});

	it("rewrites math in a link label but not in its destination", () => {
		expect(normalizeMath("[\\(a\\)](http://x/$b$)")).toBe("[$$a$$](http://x/$b$)");
	});

	it("leaves a fence after a bullet untouched and rewrites the math after it", () => {
		expect(normalizeMath("- ```sh\n  echo $HOME$\n  ```\n\nThen $x^2$.")).toBe("- ```sh\n  echo $HOME$\n  ```\n\nThen $$x^2$$.");
	});

	it("leaves a fence after a number marker untouched", () => {
		const numbered = "1. ```tex\n   $a$ \\(b\\)\n   ```";
		expect(normalizeMath(numbered)).toBe(numbered);
	});

	it("treats an unclosed fence in a list item as code to the end while it streams", () => {
		const streaming = "- ```sh\n  echo $A$ \\(b\\)";
		expect(normalizeMath(streaming)).toBe(streaming);
	});

	it("ends a fence where its blockquote ends", () => {
		expect(normalizeMath("> ```sh\n> echo $A$\n\nthen $b$")).toBe("> ```sh\n> echo $A$\n\nthen $$b$$");
	});

	it("leaves indented code untouched", () => {
		expect(normalizeMath("Run:\n\n    echo $a$ \\(b\\)\n\nthen $c$")).toBe("Run:\n\n    echo $a$ \\(b\\)\n\nthen $$c$$");
	});

	it("leaves an HTML block untouched", () => {
		expect(normalizeMath("<div>\nprice $x$ \\(y\\)\n</div>\n\nthen $z$")).toBe("<div>\nprice $x$ \\(y\\)\n</div>\n\nthen $$z$$");
	});

	it("keeps rewriting math after a candidate that starts inside code", () => {
		expect(normalizeMath("```sh\necho $$\n```\n\n\\(y\\) and $$z$$")).toBe("```sh\necho $$\n```\n\n$$y$$ and $$z$$");
	});
});

describe("ChatMarkdown math", () => {
	it("renders inline math", async () => {
		const container = renderMarkdown("Euler: $e^{i\\pi}+1=0$.");
		await waitFor(() => expect(container.querySelector(".katex")).not.toBeNull());
		expect(container.querySelector(".katex-display")).toBeNull();
	});

	it("renders display math", async () => {
		const container = renderMarkdown("Sum:\n\n$$\n\\sum_{i=1}^n i\n$$\n");
		await waitFor(() => expect(container.querySelector(".katex-display")).not.toBeNull());
	});

	it("renders \\( \\) inline and \\[ \\] as display", async () => {
		const container = renderMarkdown("Inline \\(a^2\\) and\n\n\\[b^2\\]");
		await waitFor(() => expect(container.querySelectorAll(".katex")).toHaveLength(2));
		expect(container.querySelectorAll(".katex-display")).toHaveLength(1);
	});

	it("keeps a table whose cell holds \\[ \\] math", async () => {
		const container = renderMarkdown("| a | b |\n| --- | --- |\n| 1 | \\[x^2\\] |\n| 2 | y |");
		await waitFor(() => expect(container.querySelector("td .katex")).not.toBeNull());
		expect(container.querySelectorAll("tbody tr")).toHaveLength(2);
		expect(container.querySelector(".katex-display")).toBeNull();
	});

	it("keeps a numbered list whose item holds \\[ \\] math", async () => {
		const container = renderMarkdown("1. one\n2. \\[x^2\\]\n3. three");
		await waitFor(() => expect(container.querySelector("li .katex")).not.toBeNull());
		expect(container.querySelectorAll("ol")).toHaveLength(1);
		expect(container.querySelectorAll("ol > li")).toHaveLength(3);
	});

	it("renders an escaped bracket with no math in it as the bracket", () => {
		const container = renderMarkdown("see \\[1\\]");
		expect(container.querySelector("p")?.textContent).toBe("see [1]");
		expect(container.querySelector(".katex, .math")).toBeNull();
	});

	it("leaves math inside code blocks and inline code alone", async () => {
		const container = renderMarkdown("Math $x$ here.\n\n```tex\n$y$ \\(z\\)\n```\n\nand `$w$`");
		await waitFor(() => expect(container.querySelector(".katex")).not.toBeNull());
		expect(container.querySelectorAll(".katex")).toHaveLength(1);
		expect(container.textContent).toContain("$y$ \\(z\\)");
		expect(container.textContent).toContain("$w$");
	});

	it("renders math after a fenced code block in a list item and keeps the code as written", async () => {
		const container = renderMarkdown("- ```sh\n  echo $HOME$\n  ```\n\nThen $x^2$.");
		await waitFor(() => expect(container.querySelector(".katex")).not.toBeNull());
		expect(container.querySelectorAll(".katex")).toHaveLength(1);
		expect(container.querySelector("li")?.textContent).toContain("echo $HOME$");
	});

	it("keeps dollar prices as text beside real math", async () => {
		const container = renderMarkdown("It costs $5 and $10 in total, so $n$ items.");
		await waitFor(() => expect(container.querySelector(".katex")).not.toBeNull());
		expect(container.querySelectorAll(".katex")).toHaveLength(1);
		expect(container.querySelector("p")?.textContent).toContain("It costs $5 and $10 in total, so ");
	});

	it("keeps the paragraph and the list after a multi-line \\( \\)", async () => {
		const container = renderMarkdown("\\(a +\nb\\) is the sum.\n\nNext paragraph.\n\n- list");
		await waitFor(() => expect(container.querySelector(".katex")).not.toBeNull());
		expect(container.querySelectorAll("p")).toHaveLength(2);
		expect(container.querySelector("ul > li")?.textContent).toBe("list");
	});

	it("keeps a numbered list whose item holds a $$ $$ line", async () => {
		const container = renderMarkdown("1. First:\n   $$a^2$$\n2. Second\n3. Third");
		await waitFor(() => expect(container.querySelector("li .katex-display")).not.toBeNull());
		expect(container.querySelectorAll("ol")).toHaveLength(1);
		expect(container.querySelectorAll("ol > li")).toHaveLength(3);
	});

	it("caps user-given sizes and stays quiet about LaTeX-incompatible input", async () => {
		const warn = vi.spyOn(console, "warn").mockImplementation(() => undefined);
		try {
			const container = renderMarkdown("Big $\\rule{5000em}{5000em}$ and $é$.");
			await waitFor(() => expect(container.querySelectorAll(".katex")).toHaveLength(2));
			const rule = container.querySelector<HTMLElement>(".katex .rule");
			expect(rule?.style.borderRightWidth).toBe("50em");
			expect(rule?.style.borderTopWidth).toBe("50em");
			expect(warn).not.toHaveBeenCalled();
		} finally {
			warn.mockRestore();
		}
	});

	it("renders an invalid or half-streamed expression without throwing", async () => {
		const container = renderMarkdown("Bad $\\frac{1}{$ and open\n\n$$\n\\left( x");
		await waitFor(() => expect(container.querySelector(".katex-error, .katex")).not.toBeNull());
		expect(container.textContent).toContain("Bad");
	});
});

describe("ChatMarkdown streaming caret", () => {
	const hasCaret = (paragraph: Element | undefined) => paragraph?.querySelector('[aria-hidden="true"]') != null;

	it("keeps each paragraph's DOM node across streamed frames and shows the caret only on the last paragraph", () => {
		const { wrapper } = createProvidersWrapper();
		const frame1 = "First.\n\nSecond";
		const frame2 = "First.\n\nSecond grows.\n\nThird";
		const { container, rerender } = render(<ChatMarkdown content={frame1} withCaret={true} />, { wrapper });
		const [first, second] = container.querySelectorAll("p");
		expect(hasCaret(first)).toBe(false);
		expect(hasCaret(second)).toBe(true);

		rerender(<ChatMarkdown content={frame2} withCaret={true} />);

		const paragraphs = [...container.querySelectorAll("p")];
		expect(paragraphs).toHaveLength(3);
		expect(paragraphs[0]).toBe(first);
		expect(paragraphs[1]).toBe(second);
		expect(paragraphs[1]?.textContent).toBe("Second grows.");
		expect(paragraphs.map(hasCaret)).toEqual([false, false, true]);
	});
});
