import type { CSSProperties } from "react";

/** Clearly visible highlight for the row whose detail is open in a side panel. */
export const selectedRowStyle: CSSProperties = {
  background: "rgba(59, 130, 246, 0.18)",
  boxShadow: "inset 3px 0 0 var(--color-accent)",
};
