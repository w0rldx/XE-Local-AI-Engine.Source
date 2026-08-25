import { Anchor, Badge, Button, Card, Group, ScrollArea, Stack, Table, Tabs, Text, TextInput } from "@mantine/core";
import { IconExternalLink, IconRoute, IconSearch } from "@tabler/icons-react";
import type { ChangeEvent } from "react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import {
	applicationInfo,
	type IThirdPartyPackage,
	bundledDataLegalDocuments,
	runtimeLegalDocuments,
	thirdPartyPackages,
} from "@/features/about/data/AboutData";
import { AppUpdateSection } from "@/features/app-update/components/AppUpdateSection";
import { useOnboarding } from "@/features/onboarding/context/OnboardingContext";
import { tutorialRegistry, type TutorialId } from "@/features/onboarding/data/TutorialRegistry";

export interface IAboutDialogProps {
	opened: boolean;
	onClose: () => void;
}

/**
 * About dialog for the node client. Built on the shared {@link DialogShell} so it
 * matches every other dialog in the app. Mirrors the C0re platform's About modal:
 * Application and Tutorials tabs plus a searchable third-party Licenses tab.
 */
export function AboutDialog({ opened, onClose }: IAboutDialogProps) {
	const { t } = useTranslation();
	const [licenseFilter, setLicenseFilter] = useState("");
	const [activeTab, setActiveTab] = useState<string | null>("application");
	const onboarding = useOnboarding();

	const handleTutorialAction = (tutorialId: TutorialId) => {
		if (!onboarding) {
			return;
		}
		if (onboarding.activeTutorialId !== null) {
			return;
		}
		const state = onboarding.tutorials[tutorialId];
		if (state.hasProgress) {
			onboarding.resume(tutorialId);
		} else if (state.status !== undefined) {
			onboarding.restart(tutorialId);
		} else {
			onboarding.start(tutorialId);
		}
		onClose();
	};

	const filteredPackages = useMemo(() => {
		const query = licenseFilter.trim().toLowerCase();

		if (query === "") {
			return thirdPartyPackages;
		}

		return thirdPartyPackages.filter(
			(pkg) =>
				pkg.name.toLowerCase().includes(query) ||
				pkg.license.toLowerCase().includes(query) ||
				(pkg.source ?? "").toLowerCase().includes(query),
		);
	}, [licenseFilter]);

	const sourceLabel = (source: IThirdPartyPackage["source"]) =>
		source === "backend" ? t("pages.about.sourceBackend", "Backend") : t("pages.about.sourceFrontend", "Frontend");

	const handleFilterChange = (event: ChangeEvent<HTMLInputElement>) => {
		setLicenseFilter(event.currentTarget.value);
	};

	return (
		<DialogShell opened={opened} onClose={onClose} title={t("pages.about.title", "About")} size="xl" keepMounted={true}>
			<Tabs value={activeTab} onChange={setActiveTab} px="md" pb="md">
				<Tabs.List>
					<Tabs.Tab value="application">{t("pages.about.applicationTab", "Application")}</Tabs.Tab>
					<Tabs.Tab value="tutorials">{t("pages.about.tutorialsTab", "Tutorials")}</Tabs.Tab>
					<Tabs.Tab value="licenses">{t("pages.about.licensesTab", "Licenses")}</Tabs.Tab>
				</Tabs.List>

				<Tabs.Panel value="application" pt="md">
					<Stack gap="sm">
						<Text size="xl" fw={700}>
							{applicationInfo.title}
						</Text>
						<Group gap="xs">
							<Text c="dimmed">{t("pages.about.version", "Version")}</Text>
							<Badge variant="light">{applicationInfo.version}</Badge>
						</Group>
						<Text>{applicationInfo.description}</Text>
						{applicationInfo.websiteUrl ? (
							<Anchor href={applicationInfo.websiteUrl} target="_blank" rel="noopener noreferrer">
								<Group gap={4}>
									{t("pages.about.website", "Website")}
									<IconExternalLink size={14} />
								</Group>
							</Anchor>
						) : null}
						{applicationInfo.repositoryUrl ? (
							<Anchor href={applicationInfo.repositoryUrl} target="_blank" rel="noopener noreferrer">
								<Group gap={4}>
									{t("pages.about.repository", "Repository")}
									<IconExternalLink size={14} />
								</Group>
							</Anchor>
						) : null}
						<Text size="sm" c="dimmed">
							{applicationInfo.copyright}
						</Text>
						{onboarding ? (
							<Group>
								<Button
									variant="light"
									leftSection={<IconRoute size={16} />}
									onClick={() => setActiveTab("tutorials")}
									data-testid="about-open-tutorials"
								>
									{t("onboarding.actions.openTutorials")}
								</Button>
							</Group>
						) : null}
						<AppUpdateSection />
					</Stack>
				</Tabs.Panel>

				<Tabs.Panel value="tutorials" pt="md">
					<Stack gap="md">
						<Text c="dimmed">{t("onboarding.catalog.intro")}</Text>
						{tutorialRegistry.map((tutorial) => {
							const state = onboarding?.tutorials[tutorial.id];
							const action = state?.hasProgress ? "resume" : state?.status !== undefined ? "restart" : "start";
							return (
								<Card key={tutorial.id} withBorder={true} radius="md" padding="md" data-testid={`tutorial-card-${tutorial.id}`}>
									<Stack gap="xs">
										<Group justify="space-between" align="flex-start">
											<Stack gap={2}>
												<Text fw={700}>{t(`onboarding.tutorials.${tutorial.id}.title`)}</Text>
												<Text size="sm" c="dimmed">
													{t(`onboarding.tutorials.${tutorial.id}.description`)}
												</Text>
											</Stack>
											<Badge variant="light">
												{t("onboarding.catalog.minutes", { count: tutorial.estimatedMinutes })}
											</Badge>
										</Group>
										<Group justify="space-between">
											<Badge color={state?.status === "completed" ? "green" : state?.status === "skipped" ? "gray" : "blue"}>
												{!tutorial.isAvailable
													? t("onboarding.status.unavailable")
													: t(`onboarding.status.${state?.status ?? (state?.hasProgress ? "inProgress" : "notStarted")}`)}
											</Badge>
											<Button
												size="xs"
												disabled={!onboarding || !tutorial.isAvailable || onboarding.activeTutorialId !== null}
												onClick={() => handleTutorialAction(tutorial.id)}
											>
												{t(`onboarding.actions.${action}`)}
											</Button>
										</Group>
									</Stack>
								</Card>
							);
						})}
					</Stack>
				</Tabs.Panel>

				<Tabs.Panel value="licenses" pt="md">
					<Stack gap="sm">
						<Text size="sm" c="dimmed">
							{t("pages.about.licensesIntro", "Key third-party packages used by this application.")}
						</Text>
						<Stack gap={2}>
							<Text size="sm" fw={600}>
								{t("pages.about.runtimeTerms", "Platform .NET component terms")}
							</Text>
							{runtimeLegalDocuments.map((document) => (
								<Anchor key={document.href} href={document.href} target="_blank" rel="noopener noreferrer" size="sm">
									{document.name}
								</Anchor>
							))}
						</Stack>
						<Stack gap={2}>
							<Text size="sm" fw={600}>
								{t("pages.about.bundledDataTerms", "Bundled data terms")}
							</Text>
							{bundledDataLegalDocuments.map((document) => (
								<Anchor key={document.href} href={document.href} target="_blank" rel="noopener noreferrer" size="sm">
									{document.name}
								</Anchor>
							))}
						</Stack>
						<TextInput
							value={licenseFilter}
							onChange={handleFilterChange}
							placeholder={t("pages.about.searchPackages", "Search packages")}
							leftSection={<IconSearch size={16} />}
						/>
						<ScrollArea.Autosize mah="50vh">
							<Table stickyHeader={true} highlightOnHover={true}>
								<Table.Thead>
									<Table.Tr>
										<Table.Th>{t("pages.about.packageName", "Package")}</Table.Th>
										<Table.Th w={110}>{t("pages.about.packageType", "Type")}</Table.Th>
										<Table.Th>{t("pages.about.packageVersion", "Version")}</Table.Th>
										<Table.Th>{t("pages.about.packageLicense", "License")}</Table.Th>
									</Table.Tr>
								</Table.Thead>
								<Table.Tbody>
									{filteredPackages.map((pkg) => (
										<Table.Tr key={pkg.id}>
											<Table.Td>
												{pkg.homepageUrl ? (
													<Anchor href={pkg.homepageUrl} target="_blank" rel="noopener noreferrer" size="sm">
														{pkg.name}
													</Anchor>
												) : (
													<Text size="sm">{pkg.name}</Text>
												)}
											</Table.Td>
											<Table.Td style={{ whiteSpace: "nowrap" }}>
												<Badge
													variant="light"
													size="sm"
													color={pkg.source === "backend" ? "grape" : "blue"}
													styles={{ label: { overflow: "visible" } }}
												>
													{sourceLabel(pkg.source)}
												</Badge>
											</Table.Td>
											<Table.Td>
												<Text size="sm" c="dimmed">
													{pkg.version}
												</Text>
											</Table.Td>
											<Table.Td>
												<Badge variant="light" size="sm">
													{pkg.license}
												</Badge>
											</Table.Td>
										</Table.Tr>
									))}
									{filteredPackages.length === 0 ? (
										<Table.Tr>
											<Table.Td colSpan={4}>
												<Text size="sm" c="dimmed" ta="center" py="md">
													{t("pages.about.noPackagesFound", "No packages found.")}
												</Text>
											</Table.Td>
										</Table.Tr>
									) : null}
								</Table.Tbody>
							</Table>
						</ScrollArea.Autosize>
					</Stack>
				</Tabs.Panel>
			</Tabs>
		</DialogShell>
	);
}
