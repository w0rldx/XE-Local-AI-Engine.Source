import { createContext } from "react";

import type { ConfirmContextType } from "@/core/ui/models/Confirm";

export const ConfirmContext = createContext<ConfirmContextType | undefined>(undefined);
