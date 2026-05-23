import type { TableQueryParameters } from "@/core/api/models/QueryBuilder";

export function buildTableQueryParameters(parameters: TableQueryParameters): string {
	const urlParameters = new URLSearchParams();

	// Required pagination
	urlParameters.append("page", String(parameters.page));
	urlParameters.append("pageSize", String(parameters.pageSize));

	// Optional search
	if (parameters.search) {
		urlParameters.append("globalSearch", parameters.search);
	}

	// Optional sorting
	if (parameters.sorting?.[0]) {
		urlParameters.append("sortColumn", parameters.sorting[0].id);
		urlParameters.append("sortOrder", parameters.sorting[0].desc ? "desc" : "asc");
	}

	// Optional column filters
	if (parameters.columnFilters) {
		for (const filter of parameters.columnFilters) {
			if (filter.value !== undefined && filter.value !== "") {
				urlParameters.append(`columnFilters[${filter.id}]`, String(filter.value));
			}
		}
	}

	return urlParameters.toString();
}
