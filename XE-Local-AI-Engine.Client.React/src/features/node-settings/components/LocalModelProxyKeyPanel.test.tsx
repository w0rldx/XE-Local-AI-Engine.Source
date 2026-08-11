// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { LocalModelProxyKeyPanel } from "@/features/node-settings/components/LocalModelProxyKeyPanel";

// The panel manages the INBOUND local-model-proxy credential — the key an external OpenAI-compatible tool presents
// to this node's OpenAI-compatible proxy endpoint. What is worth pinning: the key is NEVER rendered on load (the
// node stores only a hash, so the GET has none to give), it appears exactly once in the response to a generate
// action behind a copy-it-now warning, and the panel surfaces the endpoint URL and warns that regenerating replaces
// the existing key.

const getMock = vi.fn();
const generateMock = vi.fn();
const revokeMock = vi.fn();

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getLocalModelProxyApiKeyOptions: () => ({ queryKey: ["local-model-proxy-key"], queryFn: () => getMock() }),
	getLocalModelProxyApiKeyQueryKey: () => ["local-model-proxy-key"],
	generateLocalModelProxyApiKeyMutation: () => ({ mutationFn: () => generateMock() }),
	revokeLocalModelProxyApiKeyMutation: () => ({ mutationFn: () => revokeMock() }),
}));

vi.mock("@/core/api/ResponseValidation", () => ({
	withResponseValidation: (options: unknown) => options,
}));

// Deterministic i18n: t returns the supplied default (with {{var}} interpolation) so human copy is asserted rather
// than raw keys — this doubles as the check that the panel never renders a bare dotted key.
vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, fallback?: string, vars?: Record<string, unknown>) => {
			const text = fallback ?? _key;
			if (vars === undefined) {
				return text;
			}
			return Object.entries(vars).reduce(
				(acc, [name, value]) => acc.replace(new RegExp(`{{${name}}}`, "g"), String(value)),
				text,
			);
		},
	}),
}));

function installJsdomEnvironmentMocks(): void {
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
}

// Rendered inline rather than as a named <Wrapper> component: biome's useComponentExportOnlyModules rule rejects an
// unexported component declaration in a module that also exports non-components.
function renderPanel() {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider>
				<LocalModelProxyKeyPanel />
			</MantineProvider>
		</QueryClientProvider>,
	);
}

// Deliberately has no `key`: the GET response type carries none, because the node cannot recover one from a digest.
const configuredResponse = {
	configured: true,
	endpointUrl: "http://127.0.0.1:5000/api/local/v1",
	apiKey: {
		prefix: "xeproxy_abc123",
		createdAt: "2026-08-11T00:00:00+00:00",
		lastUsedAt: null,
	},
};

const generatedResponse = {
	...configuredResponse,
	key: "xeproxy_abc123-the-rest-of-the-secret",
};

const emptyResponse = {
	configured: false,
	endpointUrl: "http://127.0.0.1:5000/api/local/v1",
};

describe("LocalModelProxyKeyPanel", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("shows the empty state and a generate action when no key exists", async () => {
		getMock.mockResolvedValue(emptyResponse);

		renderPanel();

		await waitFor(() => {
			expect(screen.getByTestId("local-model-proxy-key-status").textContent).toBe("No key");
		});
		expect(screen.getByTestId("local-model-proxy-key-generate").textContent).toContain("Generate key");
		expect(screen.queryByTestId("local-model-proxy-key-value")).toBeNull();
	});

	it("never renders the key on load, only its prefix and the endpoint URL", async () => {
		getMock.mockResolvedValue(configuredResponse);

		renderPanel();

		await waitFor(() => {
			expect(screen.getByTestId("local-model-proxy-key-status").textContent).toBe("Key configured");
		});
		// The load-bearing assertion of hashed storage: a configured key is NOT recoverable, so an operator arriving
		// at this page must never be shown one. Only the non-secret prefix and the endpoint URL are rendered.
		expect(screen.queryByTestId("local-model-proxy-key-value")).toBeNull();
		expect(screen.queryByTestId("local-model-proxy-key-reveal")).toBeNull();
		expect(screen.getByTestId("local-model-proxy-key-prefix").textContent).toContain("xeproxy_abc123");
		expect(screen.getByTestId("local-model-proxy-key-card").textContent).not.toContain(
			"xeproxy_abc123-the-rest-of-the-secret",
		);
		expect(screen.getByTestId("local-model-proxy-key-endpoint").textContent).toBe(
			"http://127.0.0.1:5000/api/local/v1",
		);
	});

	it("tells the operator the configured key cannot be shown again", async () => {
		getMock.mockResolvedValue(configuredResponse);

		renderPanel();

		await waitFor(() => {
			expect(screen.getByTestId("local-model-proxy-key-not-retrievable").textContent).toContain(
				"cannot be shown again",
			);
		});
	});

	it("reveals the key exactly once after generating, behind a copy-it-now warning", async () => {
		getMock.mockResolvedValue(emptyResponse);
		generateMock.mockResolvedValue(generatedResponse);

		renderPanel();
		await waitFor(() => {
			expect(screen.getByTestId("local-model-proxy-key-status").textContent).toBe("No key");
		});
		expect(screen.queryByTestId("local-model-proxy-key-value")).toBeNull();

		fireEvent.click(screen.getByTestId("local-model-proxy-key-generate"));

		await waitFor(() => {
			expect(screen.getByTestId("local-model-proxy-key-value").textContent).toBe(
				"xeproxy_abc123-the-rest-of-the-secret",
			);
		});
		expect(screen.getByTestId("local-model-proxy-key-reveal").textContent).toContain("cannot be recovered");
	});

	it("drops the revealed key once the operator dismisses it", async () => {
		getMock.mockResolvedValue(emptyResponse);
		generateMock.mockResolvedValue(generatedResponse);

		renderPanel();
		await waitFor(() => {
			expect(screen.getByTestId("local-model-proxy-key-status").textContent).toBe("No key");
		});
		fireEvent.click(screen.getByTestId("local-model-proxy-key-generate"));
		await waitFor(() => {
			expect(screen.getByTestId("local-model-proxy-key-value")).not.toBeNull();
		});

		fireEvent.click(screen.getByTestId("local-model-proxy-key-dismiss-reveal"));

		await waitFor(() => {
			expect(screen.queryByTestId("local-model-proxy-key-value")).toBeNull();
		});
	});

	it("reports that a configured key has never been used", async () => {
		getMock.mockResolvedValue(configuredResponse);

		renderPanel();

		await waitFor(() => {
			expect(screen.getByTestId("local-model-proxy-key-last-used").textContent).toContain("Never used");
		});
	});

	it("offers regenerate once a key exists and warns that it replaces the old one", async () => {
		getMock.mockResolvedValue(configuredResponse);

		renderPanel();

		await waitFor(() => {
			expect(screen.getByTestId("local-model-proxy-key-generate").textContent).toContain("Regenerate key");
		});
		expect(screen.getByTestId("local-model-proxy-key-card").textContent).toContain("stops working");
	});

	it("calls the generate mutation when the button is pressed", async () => {
		getMock.mockResolvedValue(emptyResponse);
		generateMock.mockResolvedValue({ configured: true });

		renderPanel();
		await waitFor(() => {
			expect(screen.getByTestId("local-model-proxy-key-status").textContent).toBe("No key");
		});
		fireEvent.click(screen.getByTestId("local-model-proxy-key-generate"));

		await waitFor(() => {
			expect(generateMock).toHaveBeenCalledTimes(1);
		});
	});

	it("disables revoke when there is nothing to revoke", async () => {
		getMock.mockResolvedValue(emptyResponse);

		renderPanel();

		await waitFor(() => {
			expect((screen.getByTestId("local-model-proxy-key-revoke") as HTMLButtonElement).disabled).toBe(true);
		});
	});

	it("calls the revoke mutation when a key exists", async () => {
		getMock.mockResolvedValue(configuredResponse);
		revokeMock.mockResolvedValue(undefined);

		renderPanel();
		await waitFor(() => {
			expect((screen.getByTestId("local-model-proxy-key-revoke") as HTMLButtonElement).disabled).toBe(false);
		});
		fireEvent.click(screen.getByTestId("local-model-proxy-key-revoke"));

		await waitFor(() => {
			expect(revokeMock).toHaveBeenCalledTimes(1);
		});
	});
});
