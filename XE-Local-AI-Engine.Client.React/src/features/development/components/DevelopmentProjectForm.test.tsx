// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { NetworkError } from "@/core/api/errors/NetworkError";
import {
	DevelopmentProjectForm,
	type DevelopmentProjectFormValues,
} from "@/features/development/components/DevelopmentProjectForm";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}));

// apiErrorMessage resolves its copy through i18next's standalone `t`, which is not initialized in unit tests.
vi.mock("i18next", () => ({ t: (key: string, fallback?: string) => fallback ?? key }));

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

	it("tells its owner about the repository it auto-selects after registering", async () => {
		// Registering auto-selects the new repository, and the owner drives profile detection off that id. This was a
		// real bug: the notification fired only from the Select's onChange, so on the register-then-create path —
		// which is the first-run path — detection never ran and the command-profile confirmation step never appeared
		// at all. Only the browser E2E caught it, and only once it stopped racing the detection query.
		const register = vi.fn().mockResolvedValue({ id: "repository-2", alias: "Engine", availability: "Available" });
		const repositoryChanged = vi.fn();
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					onRegister={register}
					onRepositoryChange={repositoryChanged}
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

		await waitFor(() => expect(repositoryChanged).toHaveBeenCalledWith("repository-2"));
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

	it("offers the template picker and destination path on the first-run surface, with no project or repository", async () => {
		// The create Accordion auto-opens only when the node has zero projects, and this form is its entire body.
		// Anything gated behind an existing project or a registered repository is unreachable on a fresh node, so the
		// template entry point has to be operable from exactly this render — no repositories, no selection, no detection.
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					templates={[{ id: "template-1", alias: "Starter", availability: "Available" }]}
					onRegister={vi.fn()}
					onCreateFromTemplate={vi.fn()}
					onSubmit={vi.fn()}
				/>
			</MantineProvider>,
		);

		fireEvent.click(screen.getByTestId("development-open-create-from-template"));

		expect(await screen.findByTestId("development-template-select")).toBeTruthy();
		expect(screen.getByTestId("development-template-destination")).toBeTruthy();
		expect(screen.getByTestId("development-template-alias")).toBeTruthy();
	});

	it("creates a repository from the picked template, destination and alias, carrying the form's base branch", async () => {
		const createFromTemplate = vi.fn().mockResolvedValue({
			repository: { id: "repository-9", alias: "New engine", availability: "Available" },
			templateAlias: "Starter",
			templateCommit: "abc1234",
		});
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					templates={[{ id: "template-1", alias: "Starter", availability: "Available" }]}
					onRegister={vi.fn()}
					onCreateFromTemplate={createFromTemplate}
					onSubmit={vi.fn()}
				/>
			</MantineProvider>,
		);

		fireEvent.click(screen.getByTestId("development-open-create-from-template"));
		fireEvent.click(await screen.findByTestId("development-template-select"));
		fireEvent.click(await screen.findByRole("option", { name: "Starter", hidden: true }));
		fireEvent.change(screen.getByTestId("development-template-destination"), {
			target: { value: "/home/operator/projects/new-engine" },
		});
		fireEvent.change(screen.getByTestId("development-template-alias"), { target: { value: "New engine" } });
		fireEvent.click(screen.getByTestId("development-create-from-template"));

		await waitFor(() =>
			expect(createFromTemplate).toHaveBeenCalledWith({
				templateId: "template-1",
				destinationPath: "/home/operator/projects/new-engine",
				alias: "New engine",
				baseBranch: "main",
			}),
		);
	});

	it("tells its owner about the repository it auto-selects after creating from a template", async () => {
		// Regression for the a5028849 class of defect, on the template path this time. Creating from a template
		// auto-selects the new repository, and the owner drives command-profile detection off that id. If only
		// setValues runs, DevelopmentPage.profileFolderId stays null, detection never runs, the confirmation panel
		// never renders, and Create silently takes its "no detection" branch — on the first-run path, where the
		// operator has no other way to see the profile their project will run under.
		const createFromTemplate = vi.fn().mockResolvedValue({
			repository: { id: "repository-9", alias: "New engine", availability: "Available" },
		});
		const repositoryChanged = vi.fn();
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					templates={[{ id: "template-1", alias: "Starter", availability: "Available" }]}
					onRegister={vi.fn()}
					onRepositoryChange={repositoryChanged}
					onCreateFromTemplate={createFromTemplate}
					onSubmit={vi.fn()}
				/>
			</MantineProvider>,
		);

		fireEvent.click(screen.getByTestId("development-open-create-from-template"));
		fireEvent.click(await screen.findByTestId("development-template-select"));
		fireEvent.click(await screen.findByRole("option", { name: "Starter", hidden: true }));
		fireEvent.change(screen.getByTestId("development-template-destination"), {
			target: { value: "/home/operator/projects/new-engine" },
		});
		fireEvent.change(screen.getByTestId("development-template-alias"), { target: { value: "New engine" } });
		fireEvent.click(screen.getByTestId("development-create-from-template"));

		await waitFor(() => expect(repositoryChanged).toHaveBeenCalledWith("repository-9"));
	});

	it("blocks the template create until a template, a destination and an alias are all supplied", async () => {
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					templates={[{ id: "template-1", alias: "Starter", availability: "Available" }]}
					onRegister={vi.fn()}
					onCreateFromTemplate={vi.fn()}
					onSubmit={vi.fn()}
				/>
			</MantineProvider>,
		);

		fireEvent.click(screen.getByTestId("development-open-create-from-template"));
		const create = (await screen.findByTestId("development-create-from-template")) as HTMLButtonElement;
		expect(create.disabled).toBe(true);

		fireEvent.click(screen.getByTestId("development-template-select"));
		fireEvent.click(await screen.findByRole("option", { name: "Starter", hidden: true }));
		expect(create.disabled).toBe(true);

		fireEvent.change(screen.getByTestId("development-template-destination"), {
			target: { value: "/home/operator/projects/new-engine" },
		});
		expect(create.disabled).toBe(true);

		fireEvent.change(screen.getByTestId("development-template-alias"), { target: { value: "New engine" } });
		expect(create.disabled).toBe(false);
	});

	it("registers and removes template repositories from the same dialog", async () => {
		const addTemplate = vi.fn().mockResolvedValue({ id: "template-2", alias: "Library", availability: "Available" });
		const removeTemplate = vi.fn().mockResolvedValue(undefined);
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					templates={[{ id: "template-1", alias: "Starter", availability: "Available" }]}
					onRegister={vi.fn()}
					onCreateFromTemplate={vi.fn()}
					onAddTemplate={addTemplate}
					onRemoveTemplate={removeTemplate}
					onSubmit={vi.fn()}
				/>
			</MantineProvider>,
		);

		fireEvent.click(screen.getByTestId("development-open-create-from-template"));
		fireEvent.change(await screen.findByTestId("development-template-registry-alias"), {
			target: { value: "Library" },
		});
		fireEvent.change(screen.getByTestId("development-template-registry-path"), {
			target: { value: "/home/operator/templates/library" },
		});
		fireEvent.click(screen.getByTestId("development-template-add"));

		await waitFor(() =>
			expect(addTemplate).toHaveBeenCalledWith({ alias: "Library", hostPath: "/home/operator/templates/library" }),
		);

		const [removeButton] = screen.getAllByTestId("development-template-remove");
		if (!removeButton) {
			throw new Error("The registered-template list rendered no remove button.");
		}
		fireEvent.click(removeButton);
		await waitFor(() => expect(removeTemplate).toHaveBeenCalledWith("template-1"));
	});

	it("keeps the template dialog open and surfaces a failed create", async () => {
		const createFromTemplate = vi.fn().mockRejectedValue(new Error("The destination path is inside the node data directory."));
		render(
			<MantineProvider>
				<DevelopmentProjectForm
					repositories={[]}
					repositoriesLoading={false}
					isRegistering={false}
					isSubmitting={false}
					templates={[{ id: "template-1", alias: "Starter", availability: "Available" }]}
					onRegister={vi.fn()}
					onCreateFromTemplate={createFromTemplate}
					onSubmit={vi.fn()}
				/>
			</MantineProvider>,
		);

		fireEvent.click(screen.getByTestId("development-open-create-from-template"));
		fireEvent.click(await screen.findByTestId("development-template-select"));
		fireEvent.click(await screen.findByRole("option", { name: "Starter", hidden: true }));
		fireEvent.change(screen.getByTestId("development-template-destination"), { target: { value: "/var/lib/xe/data" } });
		fireEvent.change(screen.getByTestId("development-template-alias"), { target: { value: "New engine" } });
		fireEvent.click(screen.getByTestId("development-create-from-template"));

		expect(await screen.findByText("The destination path is inside the node data directory.")).toBeTruthy();
		expect(screen.getByTestId("development-create-from-template")).toBeTruthy();
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

	// A request that never reached the node arrives as a NetworkError, whose message is deliberately EMPTY so callers
	// localize the copy themselves. Reading `failure.message` directly rendered an empty Alert — the operator saw a
	// register button that did nothing and no reason at all. Routed through apiErrorMessage, every one of this form's
	// failure paths answers with the node-unreachable sentence instead.
	it("names the unreachable node instead of rendering an empty alert when the request never lands", async () => {
		const register = vi.fn().mockRejectedValue(new NetworkError());
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

		expect(await screen.findByText("Can't reach the node. Check that it's running and try again.")).toBeTruthy();
	});

	it("describes the isolation posture of the provider actually in force, on the control being consented to", () => {
		// F-058. Both strings were hard-coded to the process posture. Under the container provider the notice on the
		// trust checkbox was false in the UNSAFE direction — it claimed weaker isolation than was in force, on the
		// exact control the operator ticks to proceed. Asserting both directions is the point: a fix that simply
		// reworded the sentence would still be wrong for one of the two providers.
		const renderForm = (sandboxProvider?: string) =>
			render(
				<MantineProvider>
					<DevelopmentProjectForm
						repositories={[]}
						repositoriesLoading={false}
						isRegistering={false}
						isSubmitting={false}
						onRegister={vi.fn()}
						onSubmit={vi.fn()}
						sandboxProvider={sandboxProvider}
					/>
				</MantineProvider>,
			);

		// The test id sits on the checkbox input, whose textContent is empty — the acknowledgement wording lives in the
		// rendered label, which is the thing the operator actually reads.
		renderForm("process");
		expect(screen.getByTestId("development-security-notice").textContent).toContain("run as your host user");
		expect(screen.getByText(/host-user permissions/)).toBeTruthy();

		cleanup();

		renderForm("docker");
		const containerNotice = screen.getByTestId("development-security-notice").textContent ?? "";
		expect(containerNotice).not.toContain("run as your host user");
		expect(containerNotice).toContain("hardened container");
		expect(screen.getByText(/container sandbox/)).toBeTruthy();
		expect(screen.queryByText(/host-user permissions/)).toBeNull();
	});
});
