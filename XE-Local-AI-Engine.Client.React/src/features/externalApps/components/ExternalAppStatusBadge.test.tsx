// @vitest-environment jsdom

// The badge is the only place a status becomes something a user reads, and the spinner is a claim about work being
// done right now. Both are asserted per status: a missing label renders its own translation key at an operator, and a
// spinner on a settled row says the machine is busy when it is idle.

import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { ExternalAppStatusBadge } from "@/features/externalApps/components/ExternalAppStatusBadge";
import { externalAppStatuses, isExternalAppBusy } from "@/features/externalApps/models/ExternalAppModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

describe("ExternalAppStatusBadge", () => {
	it("renders a translated label for all ten statuses", () => {
		const labels = externalAppStatuses.map((status) => {
			const { unmount } = renderWithProviders(<ExternalAppStatusBadge status={status} />);
			const text = screen.getByTestId("external-app-status-badge").textContent ?? "";
			unmount();
			return text;
		});

		expect(labels).toEqual([
			"Installing",
			"Stopped",
			"Starting",
			"Running",
			"Stopping",
			"Updating",
			"Resetting",
			"Uninstalling",
			"Failed",
			"Stopped unexpectedly",
		]);
	});

	// StatusBadge renders the spinner as a Mantine Loader inside the pill, so its presence IS the in-progress claim.
	it("spins for exactly the six in-flight statuses", () => {
		const spinning = externalAppStatuses.filter((status) => {
			const { container, unmount } = renderWithProviders(<ExternalAppStatusBadge status={status} />);
			const hasLoader = container.querySelector(".mantine-Loader-root") !== null;
			unmount();
			return hasLoader;
		});

		expect(spinning).toEqual(externalAppStatuses.filter(isExternalAppBusy));
		expect(spinning).toEqual(["Installing", "Starting", "Stopping", "Updating", "Resetting", "Uninstalling"]);
	});

	// It stopped on its own and is doing nothing: it needs a human, not a spinner.
	it("does not spin for StoppedUnexpectedly", () => {
		const { container } = renderWithProviders(<ExternalAppStatusBadge status="StoppedUnexpectedly" />);

		expect(container.querySelector(".mantine-Loader-root")).toBeNull();
	});

	it("carries the translated label as its accessible name", () => {
		renderWithProviders(<ExternalAppStatusBadge status="Running" data-testid="row-status" />);

		expect(screen.getByTestId("row-status").getAttribute("aria-label")).toBe("Running");
	});
});
