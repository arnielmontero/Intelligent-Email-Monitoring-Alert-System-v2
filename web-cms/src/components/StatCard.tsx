import { Link } from "react-router-dom";

export type Tone = "neutral" | "good" | "warn" | "bad" | "info";

interface StatCardProps {
  label: string;
  value: number | string;
  hint?: string;
  tone?: Tone;
  /** Page to open when clicked. */
  to?: string;
  /** Click handler (e.g. filter the list below); ignored when `to` is set. */
  onClick?: () => void;
  active?: boolean;
}

/** A single figure: small uppercase label, large number, one-line hint. Tone only colours a thin top line and the number. */
export default function StatCard({ label, value, hint, tone = "neutral", to, onClick, active }: StatCardProps) {
  const className = `stat-card tone-${tone}${to || onClick ? " is-clickable" : ""}${active ? " is-active" : ""}`;
  const body = (
    <>
      <div className="stat-label">{label}</div>
      <div className="stat-value">{value}</div>
      {hint && <div className="stat-hint">{hint}</div>}
    </>
  );
  if (to) return <Link to={to} className={className}>{body}</Link>;
  if (onClick) return <button type="button" onClick={onClick} className={className} aria-pressed={active}>{body}</button>;
  return <div className={className}>{body}</div>;
}

/** A small status pill, e.g. "New Case", "Reply found". */
export function Badge({ tone = "neutral", children }: { tone?: Tone; children: React.ReactNode }) {
  return <span className={`badge tone-${tone}`}>{children}</span>;
}
