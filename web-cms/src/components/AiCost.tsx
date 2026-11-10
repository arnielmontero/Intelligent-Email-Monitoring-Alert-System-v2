import type { AiCostDto } from "../api/types";
import { compact, number, usd } from "../pages/ai-usage/format";

/** The plain-language breakdown shown on hover and in the AI analysis tab. */
export function describeAiCost(cost: AiCostDto): string {
  if (cost.calls === 0) return "No AI was used for this email: IEMAS's built-in filter settled it, which costs nothing.";
  const parts = [
    `${number(cost.calls)} AI call${cost.calls === 1 ? "" : "s"}${cost.failedCalls > 0 ? ` (${cost.failedCalls} failed, retried)` : ""}`,
    `${number(cost.totalTokens)} tokens (${number(cost.promptTokens)} sent, ${number(cost.completionTokens)} answered)`,
  ];
  if (cost.callsWithoutCost > 0) parts.push(`the provider reported no cost for ${cost.callsWithoutCost} of the calls, so the real cost may be slightly higher`);
  return parts.join(" · ");
}

/**
 * A small "how much did AI spend on this" tag. Emails with no AI call say so (it was free), so a missing number is
 * never ambiguous. `block` adds the written breakdown beside it, for places with room (the AI analysis tab).
 */
export default function AiCost({ cost, label = "AI", block = false }: { cost: AiCostDto | null | undefined; label?: string; block?: boolean }) {
  if (!cost) return null;
  const free = cost.calls === 0;
  return (
    <span style={{ display: "inline-flex", alignItems: "center", gap: 8, flexWrap: "wrap" }} title={describeAiCost(cost)}>
      <span
        style={{
          fontSize: 11, padding: "1px 8px", borderRadius: 999, whiteSpace: "nowrap",
          border: "1px solid var(--color-border)", color: "var(--color-text-muted)",
          background: free ? "transparent" : "var(--color-surface-alt)",
        }}
      >
        {free ? "No AI · $0" : <>{label} <strong style={{ color: "var(--color-text)", fontWeight: 600 }}>{usd(cost.costUsd, 6)}</strong>{cost.totalTokens > 0 && <> · {compact(cost.totalTokens)} tokens</>}</>}
      </span>
      {block && <span style={{ fontSize: 12, color: "var(--color-text-muted)" }}>{describeAiCost(cost)}</span>}
    </span>
  );
}
