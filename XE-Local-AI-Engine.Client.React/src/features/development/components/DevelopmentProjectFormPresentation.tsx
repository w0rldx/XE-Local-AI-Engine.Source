import type { TFunction } from "i18next";

import type { ProjectDraftSectionProps } from "@/features/development/components/DevelopmentProjectForm/ProjectDraftSection";
import { ProjectDraftSection } from "@/features/development/components/DevelopmentProjectForm/ProjectDraftSection";
import type { RepositoryRegistrationSectionProps } from "@/features/development/components/DevelopmentProjectForm/RepositoryRegistrationDialog";
import { RepositoryRegistrationDialog } from "@/features/development/components/DevelopmentProjectForm/RepositoryRegistrationDialog";
import type { TemplateRepositorySectionProps } from "@/features/development/components/DevelopmentProjectForm/TemplateRepositoryDialog";
import { TemplateRepositoryDialog } from "@/features/development/components/DevelopmentProjectForm/TemplateRepositoryDialog";

interface DevelopmentProjectFormPresentationProps {
	readonly t: TFunction;
	readonly project: ProjectDraftSectionProps;
	readonly repositoryRegistration: RepositoryRegistrationSectionProps;
	readonly templateRepository: TemplateRepositorySectionProps;
}

export function DevelopmentProjectFormPresentation(props: DevelopmentProjectFormPresentationProps) {
	return (
		<>
			<ProjectDraftSection t={props.t} project={props.project} />
			<RepositoryRegistrationDialog t={props.t} repositoryRegistration={props.repositoryRegistration} />
			<TemplateRepositoryDialog t={props.t} templateRepository={props.templateRepository} />
		</>
	);
}
