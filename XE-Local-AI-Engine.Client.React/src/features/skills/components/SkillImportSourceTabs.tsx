import { FileInput, Stack, Tabs, Text, Textarea, TextInput } from "@mantine/core";
import { IconBrandGithub, IconClipboardText, IconUpload } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

export type SkillImportSourceTab = "upload" | "github" | "paste";
const PASTE_PLACEHOLDER = "---\nname: invoice-review\ndescription: …\n---\n\n# …";

interface SkillImportSourceTabsProps {
	readonly tab: SkillImportSourceTab;
	readonly file: File | null;
	readonly owner: string;
	readonly repository: string;
	readonly markdown: string;
	readonly onTabChange: (value: SkillImportSourceTab) => void;
	readonly onFileChange: (value: File | null) => void;
	readonly onOwnerChange: (value: string) => void;
	readonly onRepositoryChange: (value: string) => void;
	readonly onMarkdownChange: (value: string) => void;
}

export function SkillImportSourceTabs(props: SkillImportSourceTabsProps) {
	const { t } = useTranslation();
	return (
		<Tabs
			value={props.tab}
			onChange={(value) => props.onTabChange((value ?? "upload") as SkillImportSourceTab)}
			keepMounted={false}
		>
			<Tabs.List>
				<Tabs.Tab value="upload" leftSection={<IconUpload size={14} />} data-testid="skill-import-tab-upload">
					{t("pages.skills.import.tabs.upload", "Upload")}
				</Tabs.Tab>
				<Tabs.Tab value="github" leftSection={<IconBrandGithub size={14} />} data-testid="skill-import-tab-github">
					{t("pages.skills.import.tabs.github", "GitHub")}
				</Tabs.Tab>
				<Tabs.Tab value="paste" leftSection={<IconClipboardText size={14} />} data-testid="skill-import-tab-paste">
					{t("pages.skills.import.tabs.paste", "Paste")}
				</Tabs.Tab>
			</Tabs.List>
			<Tabs.Panel value="upload" pt="md">
				<FileInput
					value={props.file}
					onChange={props.onFileChange}
					accept=".zip,application/zip"
					clearable={true}
					label={t("pages.skills.import.upload.label", "Skill archive (.zip)")}
					description={t(
						"pages.skills.import.upload.description",
						"A .zip containing one or more SKILL.md files with their bundled resources.",
					)}
					placeholder={t("pages.skills.import.upload.placeholder", "Choose a .zip file")}
					data-testid="skill-import-file"
				/>
			</Tabs.Panel>
			<Tabs.Panel value="github" pt="md">
				<Stack gap="sm">
					<Text size="sm" c="dimmed">
						{t(
							"pages.skills.import.github.description",
							"Only github.com is reachable, and only by owner and repository — a pasted URL is never accepted.",
						)}
					</Text>
					<TextInput
						value={props.owner}
						onChange={(event) => props.onOwnerChange(event.currentTarget.value)}
						label={t("pages.skills.import.github.owner", "Owner")}
						placeholder="microsoft"
						data-testid="skill-import-owner"
					/>
					<TextInput
						value={props.repository}
						onChange={(event) => props.onRepositoryChange(event.currentTarget.value)}
						label={t("pages.skills.import.github.repository", "Repository")}
						placeholder="skills"
						data-testid="skill-import-repository"
					/>
				</Stack>
			</Tabs.Panel>
			<Tabs.Panel value="paste" pt="md">
				<Textarea
					value={props.markdown}
					onChange={(event) => props.onMarkdownChange(event.currentTarget.value)}
					autosize={true}
					minRows={8}
					maxRows={20}
					label={t("pages.skills.import.paste.label", "SKILL.md")}
					description={t("pages.skills.import.paste.description", "Paste a complete SKILL.md, including its YAML frontmatter.")}
					placeholder={PASTE_PLACEHOLDER}
					data-testid="skill-import-markdown"
				/>
			</Tabs.Panel>
		</Tabs>
	);
}
