import type { ProblemDetails } from "@/core/api/models/ProblemDetails";

export class ApiError extends Error {
	constructor(statusCode: number, apiProblemDetails: ProblemDetails) {
		super();

		this.statusCode = statusCode;
		this.message = apiProblemDetails.detail;
		this.apiProblemDetails = apiProblemDetails;
	}

	statusCode: number;

	apiProblemDetails: ProblemDetails;
}
