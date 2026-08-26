// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}));

const navigateMock = vi.hoisted(() => vi.fn());
vi.mock("@tanstack/react-router", () => ({ useNavigate: () => navigateMock }));

const capabilityMock = vi.hoisted(() => vi.fn());
vi.mock("@/features/development/queries/useDevelopment", () => ({ useDevelopmentCapability: capabilityMock }));

import { DevelopmentConsentGate } from "@/features/development/components/DevelopmentConsentGate";
import { useDevelopmentConsentStore } from "@/features/development/stores/DevelopmentConsentStore";

const CONSENT_STORAGE_KEY = "xe-development-consent-v1";

function renderGate(sandboxProvider = "process") {
	capabilityMock.mockReturnValue({ data: { enabled: true, sandboxProvider } });
	render(
		<MantineProvider>
			<DevelopmentConsentGate>
				<div data-testid="development-page-body">page</div>
			</DevelopmentConsentGate>
		</MantineProvider>,
	);
}

// Mantine's provider reads the color scheme from matchMedia and its ScrollArea (inside DialogShell) uses a
// ResizeObserver. jsdom implements neither. Same shims the DevelopmentPage and DialogShell suites install.
beforeEach(() => {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
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
	localStorage.clear();
	// The store seeds itself from localStorage at module load, which already happened. Reset explicitly so each test
	// starts from a known acknowledgement state rather than from whichever test ran first.
	useDevelopmentConsentStore.setState({ acknowledged: false });
});

afterEach(() => {
	cleanup();
	vi.clearAllMocks();
});

describe("DevelopmentConsentGate", () => {
	it("withholds the page until the disclosure is acknowledged", () => {
		renderGate();

		expect(screen.queryByTestId("development-page-body")).toBeNull();
		expect(screen.getByText("Before you use Development Mode")).toBeTruthy();
	});

	it("keeps the continue action disabled until the operator explicitly confirms", () => {
		renderGate();

		const accept = screen.getByTestId("development-consent-accept") as HTMLButtonElement;
		expect(accept.disabled).toBe(true);

		fireEvent.click(screen.getByTestId("development-consent-checkbox"));

		expect((screen.getByTestId("development-consent-accept") as HTMLButtonElement).disabled).toBe(false);
	});

	it("reveals the page and persists the acknowledgement once confirmed", () => {
		renderGate();

		fireEvent.click(screen.getByTestId("development-consent-checkbox"));
		fireEvent.click(screen.getByTestId("development-consent-accept"));

		expect(screen.getByTestId("development-page-body")).toBeTruthy();
		expect(localStorage.getItem(CONSENT_STORAGE_KEY)).toBe("true");
	});

	// One-time, not every-visit: a notice that reappears after it was read is a notice people click through.
	it("renders the page directly when the acknowledgement was already recorded", () => {
		useDevelopmentConsentStore.setState({ acknowledged: true });
		renderGate();

		expect(screen.getByTestId("development-page-body")).toBeTruthy();
		expect(screen.queryByText("Before you use Development Mode")).toBeNull();
	});

	it("declining navigates away instead of dropping the operator onto the page", () => {
		renderGate();

		fireEvent.click(screen.getByTestId("development-consent-decline"));

		expect(navigateMock).toHaveBeenCalledWith({ to: "/" });
		expect(screen.queryByTestId("development-page-body")).toBeNull();
	});

	// The two providers put the operator in materially different positions. Describing the process provider with the
	// container sentence would understate the exposure on the exact screen that exists to state it.
	it("states the process provider's host-user posture and the conditional egress denial G1 actually delivers", () => {
		renderGate("process");

		const terms = screen.getByTestId("development-consent-terms").textContent ?? "";
		expect(terms).toContain("signed-in user account that runs the engine");

		// The pre-G1 sentence is gone, and its replacement must not swing to the opposite over-claim: the denial is
		// conditional on the backend advertising confinement, and Windows is still named as the case where it is not.
		expect(terms).not.toContain("nothing restricts what they can reach");
		expect(terms).toContain("wherever this node can enforce it");
		expect(terms).toContain("Windows");
		expect(terms).toContain("reach the network unrestricted");

		// The one sandbox that keeps egress by design, stated rather than left as a surprise.
		expect(terms).toContain("dependency restore");

		// Ceilings ARE requested now (host-toolchain profile), so the notice has to say both halves: what is enforced
		// where the node can, and that Windows still gets none.
		expect(terms).toContain("ceilings are requested for these commands wherever this node can enforce them");
		expect(terms).toContain("bounded only by its timeout and the machine");
		expect(terms).not.toContain("enforced on Linux only");
		expect(terms).not.toContain("No CPU, memory or process-count ceiling is requested");

		expect(terms).not.toContain("read-only root filesystem");
	});

	it("states the container provider's containment instead of the host-user posture", () => {
		renderGate("docker");

		const terms = screen.getByTestId("development-consent-terms").textContent ?? "";
		expect(terms).toContain("read-only root filesystem");
		expect(terms).not.toContain("signed-in user account that runs the engine");
		expect(terms).not.toContain("bounded only by its timeout and the machine");
	});

	// Containment and egress are separate facts and the notice states both — that is why the container branch carries
	// its own egress sentence rather than inheriting the process branch's. What the sentence SAYS inverted at G1: the
	// container backend advertises SupportsNetworkPolicy, so ResolveAgentFacingNetworkPolicy asks for None and gets it,
	// and the pre-G1 "nothing restricts what it can reach" is now false in the alarming direction.
	it("states the container provider's egress denial and still names the one sandbox that keeps the network", () => {
		renderGate("docker");

		const terms = screen.getByTestId("development-consent-terms").textContent ?? "";
		expect(terms).toContain("Egress is denied");
		expect(terms).not.toContain("nothing restricts what it can reach");
		expect(terms).toContain("dependency restore");
		// Containment is not weakened to make room for it — both sentences are present.
		expect(terms).toContain("all capabilities dropped");
	});

	// The one control that gates the page has to be operable the way people actually use a checkbox — by hitting the
	// sentence next to it — and has to carry that sentence as its accessible name for anyone not using a pointer.
	it("associates the confirmation sentence with the checkbox, so clicking the text ticks it", () => {
		renderGate();

		const checkbox = screen.getByRole("checkbox", {
			name: "I understand what Development Mode executes on this machine.",
		}) as HTMLInputElement;
		expect(checkbox.checked).toBe(false);

		fireEvent.click(screen.getByText("I understand what Development Mode executes on this machine."));

		expect(checkbox.checked).toBe(true);
	});

	it("says plainly that acknowledging is a disclosure and not a protection", () => {
		renderGate();

		expect(screen.getByText(/disclosure, not a protection/)).toBeTruthy();
	});

	// Nothing to consent to: the page states the disabled/unresolved case itself, and a notice about what Development
	// Mode executes would be describing something that cannot run.
	it("does not ask while the capability is unresolved or Development is disabled", () => {
		capabilityMock.mockReturnValue({ data: { enabled: false, sandboxProvider: "process" } });
		render(
			<MantineProvider>
				<DevelopmentConsentGate>
					<div data-testid="development-page-body">page</div>
				</DevelopmentConsentGate>
			</MantineProvider>,
		);

		expect(screen.getByTestId("development-page-body")).toBeTruthy();
		expect(screen.queryByText("Before you use Development Mode")).toBeNull();
	});
});
