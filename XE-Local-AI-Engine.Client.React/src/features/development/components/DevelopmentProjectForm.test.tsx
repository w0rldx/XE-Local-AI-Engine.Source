// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	DevelopmentProjectForm,
	type DevelopmentProjectFormValues,
} from "@/features/development/components/DevelopmentProjectForm";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}));

function installDomMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
		})),
	});
	// jsdom has no layout, so Mantine's Combobox keyboard-scroll helper throws asynchronously after a Select opens.
	Object.defineProperty(window.HTMLElement.prototype, "scrollIntoView", { writable: true, value: vi.fn() });
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();
			unobserve = vi.fn();
			disconnect = vi.fn();
		},
	});
}

describe("DevelopmentProjectForm", () => {
	beforeEach(() => {
		installDomMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("requires an available registered repository and explicit host-user trust", async () => {
		const submit = vi.fn();
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[
						{ id: "available", alias: "Workspace", availability: "Available" },
						{ id: "unavailable", alias: "Moved", availability: "Unavailable" },
					]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					onRegister={vi.fn()}
					onSubmit={submit}
				/>
			</MantineProvider>,
		);

		const create = screen.getByTestId("development-create-project") as HTMLButtonElement;
		expect(create.disabled).toBe(true);

		fireEvent.click(screen.getByTestId("development-repository-select"));
		fireEvent.click(await screen.findByText("Workspace"));
		expect(create.disabled).toBe(true);

		fireEvent.click(screen.getByTestId("development-trust-acknowledgement"));
		expect(create.disabled).toBe(false);
		expect(screen.getByText(/not OS isolation/)).toBeTruthy();
	});

	it("registers an absolute host path once through the shared dialog", async () => {
		const register = vi.fn().mockResolvedValue({ id: "repository-2", alias: "Engine", availability: "Available" });
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					onRegister={register}
					onSubmit={vi.fn()}
				/>
			</MantineProvider>,
		);

		fireEvent.click(screen.getByTestId("development-open-register-repository"));
		fireEvent.change(await screen.findByTestId("development-register-alias"), { target: { value: "Engine" } });
		fireEvent.change(screen.getByTestId("development-register-path"), {
			target: { value: "/home/operator/projects/engine" },
		});
		fireEvent.click(screen.getByTestId("development-register-repository"));

		await waitFor(() => expect(register).toHaveBeenCalledWith({ alias: "Engine", hostPath: "/home/operator/projects/engine" }));
	});

	it("blocks creation until the detected command profile is explicitly confirmed", async () => {
		const submit = vi.fn();
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[{ id: "available", alias: "Workspace", availability: "Available" }]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					detection={{ profileId: "dotnet-slnx", buildTarget: "Engine.slnx", candidates: ["Engine.slnx"] }}
					onRegister={vi.fn()}
					onSubmit={submit}
				/>
			</MantineProvider>,
		);

		expect(screen.getByTestId("development-profile-id").textContent).toContain("dotnet-slnx");

		const create = screen.getByTestId("development-create-project") as HTMLButtonElement;
		fireEvent.click(screen.getByTestId("development-repository-select"));
		fireEvent.click(await screen.findByText("Workspace"));
		fireEvent.click(screen.getByTestId("development-trust-acknowledgement"));
		expect(create.disabled).toBe(true);

		fireEvent.click(screen.getByTestId("development-profile-confirm"));
		expect(create.disabled).toBe(false);

		fireEvent.submit(screen.getByTestId("development-project-form"));
		expect(submit).toHaveBeenCalledWith(
			expect.objectContaining({ commandProfileId: "dotnet-slnx", buildTarget: "Engine.slnx" }),
		);
	});

	it("moves the profile with the build target when the operator picks a different candidate", async () => {
		const submit = vi.fn();
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[{ id: "available", alias: "Workspace", availability: "Available" }]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					detection={{
						profileId: "dotnet-slnx",
						buildTarget: "Engine.slnx",
						candidates: ["Engine.slnx", "src/Lib/Lib.csproj"],
					}}
					onRegister={vi.fn()}
					onSubmit={submit}
				/>
			</MantineProvider>,
		);

		fireEvent.click(screen.getByTestId("development-repository-select"));
		fireEvent.click(await screen.findByText("Workspace"));
		fireEvent.click(screen.getByTestId("development-trust-acknowledgement"));

		fireEvent.click(screen.getByTestId("development-profile-build-target"));
		fireEvent.click(await screen.findByText("src/Lib/Lib.csproj"));
		// The backend pairs profile and target strictly, so the csproj must arrive under dotnet-csproj, not dotnet-slnx.
		expect(screen.getByTestId("development-profile-id").textContent).toContain("dotnet-csproj");

		fireEvent.click(screen.getByTestId("development-profile-confirm"));
		fireEvent.submit(screen.getByTestId("development-project-form"));
		expect(submit).toHaveBeenCalledWith(
			expect.objectContaining({ commandProfileId: "dotnet-csproj", buildTarget: "src/Lib/Lib.csproj" }),
		);
	});

	it("states plainly that a generic-git repository is validated by a whitespace check alone", () => {
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[{ id: "available", alias: "Workspace", availability: "Available" }]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					detection={{ profileId: "generic-git", buildTarget: null, candidates: [] }}
					onRegister={vi.fn()}
					onSubmit={vi.fn()}
				/>
			</MantineProvider>,
		);

		expect(screen.getByTestId("development-profile-whitespace-warning").textContent).toContain(
			"validation will only check whitespace",
		);
		expect(screen.queryByTestId("development-profile-build-target")).toBeNull();
	});

	it("still allows creation when detection is unavailable, leaving the server to detect", async () => {
		const submit = vi.fn();
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[{ id: "available", alias: "Workspace", availability: "Available" }]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					detectionError="Could not inspect the repository for a build system."
					onRegister={vi.fn()}
					onSubmit={submit}
				/>
			</MantineProvider>,
		);

		expect(screen.getByTestId("development-profile-error")).toBeTruthy();
		expect(screen.queryByTestId("development-profile-confirmation")).toBeNull();

		fireEvent.click(screen.getByTestId("development-repository-select"));
		fireEvent.click(await screen.findByText("Workspace"));
		fireEvent.click(screen.getByTestId("development-trust-acknowledgement"));
		expect((screen.getByTestId("development-create-project") as HTMLButtonElement).disabled).toBe(false);

		fireEvent.submit(screen.getByTestId("development-project-form"));
		const [submitted] = submit.mock.calls[0] as [DevelopmentProjectFormValues];
		expect(submitted.commandProfileId).toBeUndefined();
		expect(submitted.buildTarget).toBeUndefined();
	});

	it("keeps the registration dialog open and surfaces contract failures", async () => {
		const register = vi.fn().mockRejectedValue(new Error("The repository registration response was incomplete."));
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					onRegister={register}
					onSubmit={vi.fn()}
				/>
			</MantineProvider>,
		);

		fireEvent.click(screen.getByTestId("development-open-register-repository"));
		fireEvent.change(await screen.findByTestId("development-register-alias"), { target: { value: "Engine" } });
		fireEvent.change(screen.getByTestId("development-register-path"), {
			target: { value: "/home/operator/projects/engine" },
		});
		fireEvent.click(screen.getByTestId("development-register-repository"));

		expect(await screen.findByText("The repository registration response was incomplete.")).toBeTruthy();
		expect(screen.getByRole("dialog")).toBeTruthy();
	});
});
