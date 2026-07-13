// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	isRemoteImageSrc,
	markdownImageUrlTransform,
	remoteImageOrigin,
} from "@/features/chat/components/MarkdownImagePolicy";
import { SafeMarkdownImage } from "@/features/chat/components/SafeMarkdownImage";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

const DATA_IMAGE = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

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

describe("markdownImageUrlTransform", () => {
	it("allows an inline data image URL for src", () => {
		expect(markdownImageUrlTransform(DATA_IMAGE, "src")).toBe(DATA_IMAGE);
	});

	it("allows a blob URL for src", () => {
		const blob = "blob:http://localhost/9f1c-abcd";
		expect(markdownImageUrlTransform(blob, "src")).toBe(blob);
	});

	it("passes a remote https URL through (consent is enforced at render, not here)", () => {
		const url = "https://cdn.example.com/photo.png";
		expect(markdownImageUrlTransform(url, "src")).toBe(url);
	});

	it("passes a protocol-relative URL through", () => {
		const url = "//cdn.example.com/photo.png";
		expect(markdownImageUrlTransform(url, "src")).toBe(url);
	});

	it("passes an api-relative URL through", () => {
		expect(markdownImageUrlTransform("/api/images/1.png", "src")).toBe("/api/images/1.png");
	});

	it("strips a javascript: scheme", () => {
		expect(markdownImageUrlTransform("javascript:alert(1)", "src")).toBe("");
	});

	it("strips a file: scheme", () => {
		expect(markdownImageUrlTransform("file:///etc/passwd", "src")).toBe("");
	});

	it("strips a non-image data URL (data:text/html is not re-allowed)", () => {
		expect(markdownImageUrlTransform("data:text/html;base64,PHNjcmlwdD4=", "src")).toBe("");
	});

	it("keeps the default secure transform for link hrefs (no data: re-allow)", () => {
		expect(markdownImageUrlTransform(DATA_IMAGE, "href")).toBe("");
		expect(markdownImageUrlTransform("javascript:alert(1)", "href")).toBe("");
	});
});

describe("isRemoteImageSrc", () => {
	it("treats an off-origin https URL as remote", () => {
		expect(isRemoteImageSrc("https://evil.example.com/x.png")).toBe(true);
	});

	it("treats a protocol-relative URL as remote", () => {
		expect(isRemoteImageSrc("//evil.example.com/x.png")).toBe(true);
	});

	it("treats a same-origin absolute URL as local", () => {
		expect(isRemoteImageSrc(`${globalThis.location.origin}/api/images/1.png`)).toBe(false);
	});

	it("treats data:, blob:, and relative sources as local", () => {
		expect(isRemoteImageSrc(DATA_IMAGE)).toBe(false);
		expect(isRemoteImageSrc("blob:http://localhost/abc")).toBe(false);
		expect(isRemoteImageSrc("/api/images/1.png")).toBe(false);
	});
});

describe("remoteImageOrigin", () => {
	it("returns the origin of an absolute URL", () => {
		expect(remoteImageOrigin("https://cdn.example.com/a/b/c.png?x=1")).toBe("https://cdn.example.com");
	});
});

describe("SafeMarkdownImage consent flow", () => {
	it("hides a remote image behind a consent placeholder showing its origin, then loads it on click", () => {
		renderWithProviders(<SafeMarkdownImage src="https://tracker.example.com/pixel.png" alt="a picture" />);

		// Blocked initially: no <img>, a consent control naming the remote origin.
		expect(screen.queryByRole("img")).toBeNull();
		const consent = screen.getByTestId("remote-image-consent");
		expect(consent).toBeTruthy();
		expect(screen.getByText("https://tracker.example.com")).toBeTruthy();

		fireEvent.click(consent);

		// After consent: the image loads with referrer suppressed and the placeholder is gone.
		const img = screen.getByRole("img");
		expect(img.getAttribute("src")).toBe("https://tracker.example.com/pixel.png");
		expect(img.getAttribute("referrerpolicy")).toBe("no-referrer");
		expect(screen.queryByTestId("remote-image-consent")).toBeNull();
	});

	it("does not carry consent to a new source when the same element's src changes", () => {
		const { rerender } = renderWithProviders(
			<SafeMarkdownImage src="https://a.example.com/one.png" alt="one" />,
		);

		// Consent to source A: the image loads.
		fireEvent.click(screen.getByTestId("remote-image-consent"));
		expect(screen.getByRole("img").getAttribute("src")).toBe("https://a.example.com/one.png");

		// Same component position, new remote source B: prior consent must not leak — the placeholder returns.
		rerender(
			<MantineProvider>
				<SafeMarkdownImage src="https://b.example.com/two.png" alt="two" />
			</MantineProvider>,
		);
		expect(screen.queryByRole("img")).toBeNull();
		const consent = screen.getByTestId("remote-image-consent");
		expect(screen.getByText("https://b.example.com")).toBeTruthy();

		// Consenting again loads source B.
		fireEvent.click(consent);
		expect(screen.getByRole("img").getAttribute("src")).toBe("https://b.example.com/two.png");
	});

	it("renders a data: image directly without a consent placeholder", () => {
		renderWithProviders(<SafeMarkdownImage src={DATA_IMAGE} alt="inline" />);

		expect(screen.queryByTestId("remote-image-consent")).toBeNull();
		const img = screen.getByRole("img");
		expect(img.getAttribute("src")).toBe(DATA_IMAGE);
		// A local image carries no referrerPolicy override.
		expect(img.getAttribute("referrerpolicy")).toBeNull();
	});

	it("renders nothing when the src was stripped to empty (dangerous scheme)", () => {
		const { container } = renderWithProviders(<SafeMarkdownImage src="" alt="x" />);
		expect(container.querySelector("img")).toBeNull();
		expect(screen.queryByTestId("remote-image-consent")).toBeNull();
	});
});
