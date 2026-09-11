// @vitest-environment jsdom

// This panel is the disclosure an operator accepts before an application is installed, so the assertions here are
// about WHAT IT CLAIMS, not about layout. Two claims in particular: it never says the network is denied (V1 enforces
// no outbound restriction, so such a line would be false), and it never invents wording for a permission name it does
// not know.

import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { PermissionsPanel } from "@/features/externalApps/components/PermissionsPanel";
import { externalAppPermissionNames } from "@/features/externalApps/models/ExternalAppModels";
import { externalAppEffectivePermissions, externalAppPermissions } from "@/features/externalApps/test/ExternalAppFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

describe("PermissionsPanel", () => {
	it("states network access as a fact and never as a denial, for both localNetwork values", () => {
		for (const localNetwork of [true, false]) {
			const { unmount } = renderWithProviders(<PermissionsPanel permissions={externalAppPermissions({ localNetwork })} />);

			expect(screen.getByTestId("external-app-permission-internet").textContent).toBe("Internet access — yes");
			expect(screen.getByTestId("external-app-permission-localNetwork").textContent).toContain(
				"this application can reach services on this computer",
			);

			// The denial wording is allowed about files, the GPU, XE's data and devices — never about the network.
			const panel = screen.getByTestId("external-app-permissions");
			for (const line of Array.from(panel.querySelectorAll('[data-testid^="external-app-permission-"]'))) {
				const text = line.textContent ?? "";
				if (text.toLowerCase().includes("network") || text.toLowerCase().includes("internet")) {
					expect(text.toLowerCase()).not.toContain("denied");
					expect(text.toLowerCase()).not.toContain("no access");
				}
			}
			unmount();
		}
	});

	it("puts host files and the graphics card in the does-not-get block when they are unset", () => {
		renderWithProviders(<PermissionsPanel permissions={externalAppPermissions()} />);

		expect(screen.getByTestId("external-app-permission-hostFiles").textContent).toBe("No access to your personal files");
		expect(screen.getByTestId("external-app-permission-gpu").textContent).toBe("No access to the graphics card");
	});

	it("names the granted level when host files and the graphics card are set", () => {
		renderWithProviders(<PermissionsPanel permissions={externalAppPermissions({ hostFiles: "readWrite", gpu: "required" })} />);

		expect(screen.getByTestId("external-app-permission-hostFiles").textContent).toBe(
			"Read and write access to the files you choose",
		);
		expect(screen.getByTestId("external-app-permission-gpu").textContent).toBe("Use of the graphics card");
	});

	it("always states the loopback and no-XE-data facts", () => {
		renderWithProviders(<PermissionsPanel permissions={externalAppPermissions()} />);

		expect(screen.getByTestId("external-app-permission-loopbackOnly").textContent).toBe("Reachable only from this computer");
		expect(screen.getByTestId("external-app-permission-noXeData").textContent).toBe(
			"No access to your XE data, models or conversations",
		);
		expect(screen.getByTestId("external-app-permission-noDevices").textContent).toBe("No access to connected devices");
	});

	// The alert is what tells an operator the update widened access at all, so it must name every one of the eight —
	// an alert that covered only the four part-only names stayed silent for a newly granted graphics card.
	it("names every one of the eight closed permission names in the changed-access alert", () => {
		for (const name of externalAppPermissionNames) {
			const { unmount } = renderWithProviders(
				<PermissionsPanel permissions={externalAppPermissions()} addedPermissions={[name]} />,
			);

			const line = screen.getByTestId(`external-app-permission-added-${name}`);
			expect(line.getAttribute("data-added")).toBe("true");
			expect((line.textContent ?? "").length).toBeGreaterThan(0);
			expect(screen.getByTestId("external-app-permissions-added").textContent).toContain("This update asks for more access");
			unmount();
		}
	});

	it("highlights the standing row of the four names that have one", () => {
		for (const name of ["internet", "localNetwork", "hostFiles", "gpu"] as const) {
			const { unmount } = renderWithProviders(
				<PermissionsPanel permissions={externalAppPermissions()} addedPermissions={[name]} />,
			);

			const row = screen.getByTestId(`external-app-permission-${name}`);
			expect(row.getAttribute("data-added")).toBe("true");
			unmount();
		}
	});

	it("gives the four part-only names their own added line and renders nothing for an unknown name", () => {
		const { unmount } = renderWithProviders(
			<PermissionsPanel
				permissions={externalAppPermissions()}
				addedPermissions={["capabilities", "writableRootFilesystem", "publishedPorts", "extraHosts"]}
			/>,
		);
		const added = screen.getByTestId("external-app-permissions-added");
		expect(added.textContent).toContain("Additional system privileges");
		expect(added.textContent).toContain("Permission to change its own program files");
		expect(added.textContent).toContain("Another address on this computer");
		expect(added.textContent).toContain("Additional name entries for this computer");
		unmount();

		renderWithProviders(<PermissionsPanel permissions={externalAppPermissions()} addedPermissions={["seccompProfile"]} />);
		expect(screen.queryByTestId("external-app-permissions-added")).toBeNull();
		expect(screen.getByTestId("external-app-permissions").querySelector('[data-added="true"]')).toBeNull();
	});

	// The disclosure describes the manifest being CONSENTED TO. Reading the installed manifest here rendered a newly
	// granted graphics card as "no access" under "It does not get" while highlighting it as new access — the panel
	// contradicting itself on the one screen where the operator is asked to agree.
	it("reads host files and the graphics card from the effective permissions, not from the installed manifest", () => {
		renderWithProviders(
			<PermissionsPanel
				permissions={externalAppPermissions({ hostFiles: "none", gpu: "none" })}
				effectivePermissions={externalAppEffectivePermissions({ hostFiles: "read", gpu: "required" })}
				addedPermissions={["gpu"]}
			/>,
		);

		const gpu = screen.getByTestId("external-app-permission-gpu");
		expect(gpu.textContent).toBe("Use of the graphics card");
		expect(gpu.getAttribute("data-added")).toBe("true");
		expect(screen.getByTestId("external-app-permission-hostFiles").textContent).toBe("Read access to the files you choose");
		expect(screen.getByTestId("external-app-permissions-added").textContent).toContain("Use of the graphics card");
	});

	it("renders one part block per service, naming a grant only one of the two services holds", () => {
		renderWithProviders(
			<PermissionsPanel permissions={externalAppPermissions()} effectivePermissions={externalAppEffectivePermissions()} />,
		);

		const web = screen.getByTestId("external-app-permission-part-web");
		expect(web.textContent).toContain("Part: web");
		expect(web.textContent).toContain("Additional system privileges");
		expect(web.textContent).toContain("Another address on this computer");

		// `worker` holds neither, which is exactly the case an application-level block hides.
		const worker = screen.getByTestId("external-app-permission-part-worker");
		expect(worker.textContent).toContain("Part: worker");
		expect(worker.textContent).toContain("Nothing beyond what is listed above");
	});

	it("names a writable root filesystem and extra hosts on the part that holds them", () => {
		renderWithProviders(
			<PermissionsPanel
				permissions={externalAppPermissions()}
				effectivePermissions={externalAppEffectivePermissions({
					services: {
						web: { capabilities: [], writableRootFilesystem: true, publishedPorts: [], extraHosts: ["db:127.0.0.1"] },
					},
				})}
			/>,
		);

		const web = screen.getByTestId("external-app-permission-part-web");
		expect(web.textContent).toContain("Permission to change its own program files");
		expect(web.textContent).toContain("Additional name entries for this computer");
	});

	// The four aggregates are the SERVER's verdict. This fixture makes them disagree with the services map on purpose:
	// a panel that unioned the map would render the two lines the map holds and neither of the two it does not.
	it("states the application-level grants from the server aggregates, never from a union of the parts", () => {
		renderWithProviders(
			<PermissionsPanel
				permissions={externalAppPermissions()}
				effectivePermissions={externalAppEffectivePermissions({
					capabilities: [],
					writableRootFilesystem: true,
					publishedPorts: [],
					extraHosts: ["db:127.0.0.1"],
				})}
			/>,
		);

		expect(screen.getByTestId("external-app-permission-effective-writableRootFilesystem").textContent).toContain(
			"Permission to change its own program files",
		);
		expect(screen.getByTestId("external-app-permission-effective-extraHosts").textContent).toContain(
			"Additional name entries for this computer",
		);
		expect(screen.queryByTestId("external-app-permission-effective-capabilities")).toBeNull();
		expect(screen.queryByTestId("external-app-permission-effective-publishedPorts")).toBeNull();
	});

	it("renders no part block at all when effectivePermissions is omitted", () => {
		renderWithProviders(<PermissionsPanel permissions={externalAppPermissions()} />);

		expect(screen.queryByTestId("external-app-permissions-effective")).toBeNull();
		expect(screen.queryByTestId("external-app-permission-part-web")).toBeNull();
		expect(screen.queryByTestId("external-app-permission-effective-capabilities")).toBeNull();
	});
});
