import { Anchor, Badge, Button, Group, ScrollArea, Stack, Table, Tabs, Text, TextInput } from "@mantine/core";
import { IconExternalLink, IconRoute, IconSearch } from "@tabler/icons-react";
import type { ChangeEvent } from "react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { applicationInfo, thirdPartyPackages } from "@/features/about/data/AboutData";
import { AppUpdateSection } from "@/features/app-update/components/AppUpdateSection";
import { useOnboarding } from "@/features/onboarding/context/OnboardingContext";

export interface IAboutDialogProps {
	opened: boolean;
	onClose: () => void;
}

/**
 * About dialog for the node client. Built on the shared {@link DialogShell} so it
 * matches every other dialog in the app. Mirrors the C0re platform's About modal:
 * an Application tab plus a searchable third-party Licenses tab.
 */
export function AboutDialog({ opened, onClose }: IAboutDialogProps) {
	const { t } = useTranslation();
	const [licenseFilter, setLicenseFilter] = useState("");
	// Restart the onboarding tour on demand. The control hides when no OnboardingProvider is mounted so the
	// dialog stays usable in isolation (e.g. tests) — the tour is purely additive.
	const onboarding = useOnboarding();

	const handleRestartTutorial = () => {
		onboarding?.start();
		onClose();
	};

	const filteredPackages = useMemo(() => {
		const query = licenseFilter.trim().toLowerCase();

		if (query === "") {
			return thirdPartyPackages;
		}

		return thirdPartyPackages.filter(
			(pkg) => pkg.name.toLowerCase().includes(query) || pkg.license.toLowerCase().includes(query),
		);
	}, [licenseFilter]);

	const handleFilterChange = (event: ChangeEvent<HTMLInputElement>) => {
		setLicenseFilter(event.currentTarget.value);
	};

	return (
		<DialogShell opened={opened} onClose={onClose} title={t("pages.about.title", "About")} size="xl">
			<Tabs defaultValue="application" px="md" pb="md">
				<Tabs.List>
					<Tabs.Tab value="application">{t("pages.about.applicationTab", "Application")}</Tabs.Tab>
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
									onClick={handleRestartTutorial}
									data-testid="about-restart-tutorial"
								>
									{t("onboarding.restart")}
								</Button>
							</Group>
						) : null}
						<AppUpdateSection />
					</Stack>
				</Tabs.Panel>

				<Tabs.Panel value="licenses" pt="md">
					<Stack gap="sm">
						<Text size="sm" c="dimmed">
							{t("pages.about.licensesIntro", "Third-party packages bundled with this application.")}
						</Text>
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
											<Table.Td colSpan={3}>
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
