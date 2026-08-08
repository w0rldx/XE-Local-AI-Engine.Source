export interface ProblemDetails {
	type: string;
	title: string;
	status: number;
	detail: string;
}

export interface ConflictProblemDetails extends ProblemDetails {
	conflictType: string;
}

export interface LoginProblemDetails extends ProblemDetails {
	loginProblemType: string;
}
