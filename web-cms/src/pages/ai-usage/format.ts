export const PURPOSE_LABELS: Record<string, string> = {
  EmailClassification: "Email classification",
  ResolutionAssessment: "Resolution check",
  ModelTest: "Model test",
  ProfileTest: "Profile test",
  ModelBenchmark: "Model analysis",
};

export function purposeLabel(purpose: string) {
  return PURPOSE_LABELS[purpose] ?? purpose;
}

export function usd(value: number | null | undefined, digits = 4) {
  if (value === null || value === undefined) return "—";
  if (value === 0) return "$0";
  return value < 0.0001 ? `$${value.toFixed(6)}` : `$${value.toFixed(digits)}`;
}

/** Spend axis ticks: short, no trailing zeros ($0.002, $0.05, $1.50). */
export function usdTick(value: number) {
  if (value === 0) return "$0";
  if (value >= 100) return `$${Math.round(value)}`;
  if (value >= 1) return `$${Number(value.toFixed(2))}`;
  if (value >= 0.01) return `$${Number(value.toFixed(3))}`;
  return `$${Number(value.toFixed(5))}`;
}

export function number(value: number | null | undefined) {
  return value === null || value === undefined ? "—" : value.toLocaleString();
}

/** 1,284 / 12.9K / 3.4M: for tiles and axes where full digits would not fit. */
export function compact(value: number) {
  const abs = Math.abs(value);
  if (abs >= 1_000_000) return `${Number((value / 1_000_000).toFixed(1))}M`;
  if (abs >= 10_000) return `${Number((value / 1_000).toFixed(1))}K`;
  return Math.round(value).toLocaleString();
}

export function shortDate(iso: string) {
  const [y, m, d] = iso.split("-").map(Number);
  return new Date(y, m - 1, d).toLocaleDateString(undefined, { day: "numeric", month: "short" });
}

export function longDate(iso: string) {
  const [y, m, d] = iso.split("-").map(Number);
  return new Date(y, m - 1, d).toLocaleDateString(undefined, { weekday: "short", day: "numeric", month: "short", year: "numeric" });
}
