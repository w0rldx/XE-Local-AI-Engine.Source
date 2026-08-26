import type { DevelopmentRepository } from "@/features/development/models/DevelopmentModels";

export interface RegisterDevelopmentRepositoryValues {
	readonly alias: string;
	readonly hostPath: string;
}

export interface RegisterDevelopmentTemplateValues {
	readonly alias: string;
	readonly hostPath: string;
}

export interface CreateDevelopmentRepositoryFromTemplateValues {
	readonly templateId: string;
	readonly destinationPath: string;
	readonly alias: string;
	readonly baseBranch: string;
}

export interface CreatedDevelopmentRepositoryFromTemplate {
	readonly repository: DevelopmentRepository;
	readonly templateAlias?: string;
	readonly templateCommit?: string;
}

export interface DevelopmentProjectFormValues {
	readonly selectedFolderId: string;
	readonly objective: string;
	readonly baseBranch: string;
	readonly taskTitle: string;
	readonly requirements: string;
	readonly acceptanceCriteriaJson: string;
	readonly egressPolicy: "LocalOnly" | "CloudScoped";
	readonly coderModelId: string;
	readonly reviewerModelId: string;
	readonly trustedRepositoryAcknowledged: boolean;
	readonly maxTokens?: number;
	readonly maxDurationSeconds?: number;
	/** The profile the operator confirmed. Omitted when detection never loaded, which asks the server to detect. */
	readonly commandProfileId?: string;
	readonly buildTarget?: string;
}
