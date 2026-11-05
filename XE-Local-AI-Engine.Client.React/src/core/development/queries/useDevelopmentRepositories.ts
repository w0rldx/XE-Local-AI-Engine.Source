import { useQuery } from "@tanstack/react-query";

import { listDevelopmentRepositoriesOptions } from "@/core/api/generated/@tanstack/react-query.gen";

export interface DevelopmentRepository {
	readonly id: string;
	readonly alias: string;
	readonly availability: string;
}

export function useDevelopmentRepositories(enabled = true) {
	return useQuery({
		...listDevelopmentRepositoriesOptions(),
		enabled,
		select: (data): readonly DevelopmentRepository[] =>
			(data.items ?? []).flatMap((repository) =>
				repository.id && repository.alias
					? [
							{
								id: repository.id,
								alias: repository.alias,
								availability: repository.availability ?? "Unavailable",
							},
						]
					: [],
			),
	});
}
