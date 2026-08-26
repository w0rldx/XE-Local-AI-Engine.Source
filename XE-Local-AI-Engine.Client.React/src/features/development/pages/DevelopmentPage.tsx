import { Alert, Grid, Group, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconCode } from "@tabler/icons-react";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { DevelopmentAttemptsTable } from "@/features/development/components/DevelopmentAttemptsTable";
import { DevelopmentContainerRuntimePanel } from "@/features/development/components/DevelopmentContainerRuntimePanel";
import { DevelopmentEventTimeline } from "@/features/development/components/DevelopmentEventTimeline";
import { DevelopmentLivePanel } from "@/features/development/components/DevelopmentLivePanel";
import { DevelopmentPatchApplyPanel } from "@/features/development/components/DevelopmentPatchApplyPanel";
import { DevelopmentProjectList } from "@/features/development/components/DevelopmentProjectList";
import { DevelopmentProjectOverview } from "@/features/development/components/DevelopmentProjectOverview";
import { DevelopmentProjectSetup } from "@/features/development/components/DevelopmentProjectSetup";
import { SandboxIsolationPanel } from "@/features/development/components/SandboxIsolationPanel";
import { useDevelopmentPageController } from "@/features/development/hooks/useDevelopmentPageController";

export function DevelopmentPage() {
	const {
		t,
		capabilityQuery,
		developmentEnabled,
		containerRuntime,
		sandboxIsolation,
		sandboxProvider,
		confirmContainerRuntimeMutation,
		repositoriesQuery,
		templatesQuery,
		projectsQuery,
		selectedProjectId,
		setSelectedProjectId,
		reconnectFolderId,
		setReconnectFolderId,
		previewTaskId,
		setPreviewTaskId,
		profileFolderId,
		setProfileFolderId,
		detectionQuery,
		projectQuery,
		repositories,
		templates,
		projects,
		detail,
		task,
		attempts,
		artifacts,
		events,
		latestAttempt,
		activeAttempt,
		nextActionKey,
		nextActionDefault,
		live,
		projectRepository,
		repositoryConnectionRequired,
		repositoryReady,
		reconnectOptions,
		registerRepository,
		createRepositoryFromTemplate,
		addTemplate,
		removeTemplate,
		createProject,
		startNext,
		cancelActive,
		preview,
		apply,
		reconnectRepository,
		registerMutation,
		createMutation,
		reconnectMutation,
		startMutation,
		cancelMutation,
		previewMutation,
		applyMutation,
	} = useDevelopmentPageController();
	if (capabilityQuery.isLoading) {
		return (
			<PageShell>
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.development.loading.capability", "Loading Development capability")}</Text>
				</Group>
			</PageShell>
		);
	}

	if (capabilityQuery.error || !developmentEnabled) {
		return (
			<PageShell>
				<Alert color={capabilityQuery.error ? "red" : "yellow"} icon={<IconAlertTriangle size={16} />}>
					{capabilityQuery.error
						? apiErrorMessage(capabilityQuery.error, "Could not verify whether Development Mode is available.")
						: t("pages.development.disabled", "Development Mode is disabled by this node's runtime configuration.")}
				</Alert>

				{/*
				 * Rendered on the disabled branch too. Development Mode being off says nothing about how AgentHome and
				 * `run_python` execute, and this is the one screen that reports that — hiding it here would leave an
				 * operator with no way to see their agent sandbox's posture at all.
				 */}
				<SandboxIsolationPanel roles={sandboxIsolation} />
			</PageShell>
		);
	}

	return (
		<PageShell>
			{/*
			 * Above the page body rather than replacing it. ADR 0004 makes a container runtime a hard requirement for
			 * Development Mode execution, but execution has not moved to the container provider yet, so blocking the
			 * page on this preflight would break the workflow that ships today. The panel says so explicitly rather
			 * than leaving the operator to reconcile a red banner with a page that plainly works.
			 */}
			<DevelopmentContainerRuntimePanel
				runtime={containerRuntime}
				sandboxProvider={sandboxProvider}
				onConfirm={(daemonId) => confirmContainerRuntimeMutation.mutate({ body: { daemonId } })}
				confirming={confirmContainerRuntimeMutation.isPending}
				confirmError={
					confirmContainerRuntimeMutation.error
						? apiErrorMessage(confirmContainerRuntimeMutation.error, "Could not confirm the container runtime.")
						: undefined
				}
			/>

			<SandboxIsolationPanel roles={sandboxIsolation} />

			<PageHeader
				icon={<IconCode size={24} />}
				title={t("pages.development.title", "Development Mode")}
				subtitle={t(
					"pages.development.subtitle",
					"Run one durable coder → validation → independent review → explicit apply workflow outside Chat.",
				)}
			/>

			<DevelopmentProjectSetup
				form={{
					detection: profileFolderId ? (detectionQuery.data ?? null) : null,
					detectionError: detectionQuery.error
						? apiErrorMessage(
								detectionQuery.error,
								t("pages.development.errors.profileDetection", "Could not inspect the repository for a build system."),
							)
						: undefined,
					detectionLoading: detectionQuery.isFetching,
					error: createMutation.error
						? apiErrorMessage(
								createMutation.error,
								t("pages.development.errors.create", "Could not create the Development project."),
							)
						: undefined,
					isRegistering: registerMutation.isPending,
					isSubmitting: createMutation.isPending,
					onAddTemplate: addTemplate,
					onCreateFromTemplate: createRepositoryFromTemplate,
					onRegister: registerRepository,
					onRemoveTemplate: removeTemplate,
					onRepositoryChange: setProfileFolderId,
					onSubmit: createProject,
					repositories,
					repositoriesError: repositoriesQuery.error
						? apiErrorMessage(repositoriesQuery.error, "Could not load registered Development repositories.")
						: undefined,
					repositoriesLoading: repositoriesQuery.isLoading,
					sandboxProvider,
					templates,
					templatesLoading: templatesQuery.isLoading,
				}}
				projects={{
					count: projects.length,
					error: projectsQuery.error ? apiErrorMessage(projectsQuery.error, "Could not load Development projects.") : undefined,
					loading: projectsQuery.isLoading,
				}}
			/>
			{projects.length > 0 ? (
				<Grid>
					<Grid.Col span={{ base: 12, lg: 3 }}>
						<DevelopmentProjectList
							error={
								projectQuery.error ? apiErrorMessage(projectQuery.error, "Could not load the Development project.") : undefined
							}
							loading={projectQuery.isLoading}
							onSelect={(id) => {
								setSelectedProjectId(id);
								setReconnectFolderId(null);
								setPreviewTaskId(null);
							}}
							projects={projects}
							selectedId={selectedProjectId}
						/>
					</Grid.Col>

					<Grid.Col span={{ base: 12, lg: 9 }}>
						{detail?.project && task ? (
							<Stack gap="lg" data-testid="development-project-detail">
								<DevelopmentProjectOverview
									attempt={{ active: activeAttempt !== null, cancel: cancelActive, canceling: cancelMutation.isPending }}
									nextAction={{
										error: startMutation.error
											? apiErrorMessage(startMutation.error, "Could not start the next action.")
											: undefined,
										label: [nextActionKey, nextActionDefault],
										run: startNext,
										running: startMutation.isPending,
									}}
									project={detail.project}
									reconnect={{
										error: reconnectMutation.error
											? apiErrorMessage(reconnectMutation.error, "Could not reconnect the repository.")
											: undefined,
										folderId: reconnectFolderId,
										loading: reconnectMutation.isPending,
										options: reconnectOptions,
										run: reconnectRepository,
										select: setReconnectFolderId,
									}}
									repository={{
										connectedAlias: projectRepository?.alias,
										connectionRequired: repositoryConnectionRequired,
										loading: repositoriesQuery.isLoading,
										ready: repositoryReady,
									}}
									task={task}
								/>

								<SectionCard>
									<DevelopmentLivePanel
										attempt={activeAttempt ?? latestAttempt}
										live={live}
										artifacts={artifacts}
										events={events}
									/>
								</SectionCard>

								<SectionCard title={t("pages.development.attempts.title", "Attempts")}>
									<DevelopmentAttemptsTable attempts={attempts} />
								</SectionCard>

								{task.status === "AwaitingApply" ? (
									<DevelopmentPatchApplyPanel
										apply={{
											loading: applyMutation.isPending,
											outcome:
												applyMutation.data?.outcome ??
												(applyMutation.data ? t("pages.development.apply.applied", "Patch applied.") : null),
											run: apply,
										}}
										preview={{
											data: previewTaskId === task.id ? previewMutation.data : undefined,
											loading: previewMutation.isPending,
											run: preview,
										}}
										repositoryReady={repositoryReady}
									/>
								) : null}

								<DevelopmentEventTimeline events={events} onRefresh={() => projectQuery.refetch()} />
							</Stack>
						) : null}
					</Grid.Col>
				</Grid>
			) : null}
		</PageShell>
	);
}
