import { useEffect, useRef, useState } from "react";

/**
 * A small column chart (one series, or stacked series) drawn as SVG, following the data-viz rules used across the CMS:
 * one axis, thin columns (at most 24px) with a 4px rounded top and a square baseline, a 2px surface gap between
 * stacked segments, hairline gridlines, text in text colours (never the series colour), a legend whenever there are
 * two or more series, and a hover/keyboard tooltip on every column. Series colours come from CSS variables so the
 * light and dark themes each use their own validated step.
 */
export interface ChartSeries {
  key: string;
  label: string;
  /** A CSS colour, normally a theme variable such as "var(--series-1)". */
  color: string;
}

export interface ChartPoint {
  id: string;
  /** Tooltip title, e.g. "Mon 10 Nov". */
  title: string;
  /** Short x-axis label, e.g. "10 Nov". */
  axisLabel: string;
  values: Record<string, number>;
  /** Extra tooltip lines in muted text, e.g. "12 calls". */
  details?: string[];
}

interface ColumnChartProps {
  points: ChartPoint[];
  series: ChartSeries[];
  /** Full-precision value for the tooltip. */
  formatValue: (value: number) => string;
  /** Compact value for the y-axis. */
  formatTick: (value: number) => string;
  ariaLabel: string;
  emptyMessage?: string;
}

const DEFAULT_WIDTH = 760;
const H = 230;
const M = { top: 10, right: 10, bottom: 26, left: 58 };
const MAX_BAR = 24;
const GAP = 2;

/** 4-5 clean gridlines: the step is 1, 2, 2.5, 5 or 10 times a power of ten. */
function niceScale(max: number): { top: number; ticks: number[] } {
  if (!(max > 0)) return { top: 1, ticks: [0] };
  const raw = max / 4;
  const magnitude = Math.pow(10, Math.floor(Math.log10(raw)));
  const normalized = raw / magnitude;
  const step = (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 2.5 ? 2.5 : normalized <= 5 ? 5 : 10) * magnitude;
  const count = Math.ceil(max / step - 1e-9);
  const ticks = Array.from({ length: count + 1 }, (_, i) => Number((i * step).toPrecision(12)));
  return { top: ticks[ticks.length - 1], ticks };
}

/** A rectangle with only its top corners rounded (the data end); the baseline end stays square. */
function roundedTop(x: number, y: number, w: number, h: number, radius: number) {
  const r = Math.max(0, Math.min(radius, w / 2, h));
  if (r === 0) return `M${x},${y + h}V${y}H${x + w}V${y + h}Z`;
  return `M${x},${y + h}V${y + r}Q${x},${y} ${x + r},${y}H${x + w - r}Q${x + w},${y} ${x + w},${y + r}V${y + h}Z`;
}

export default function ColumnChart({ points, series, formatValue, formatTick, ariaLabel, emptyMessage }: ColumnChartProps) {
  const [active, setActive] = useState<number | null>(null);
  const [containerWidth, setContainerWidth] = useState(DEFAULT_WIDTH);
  const wrapRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = wrapRef.current;
    if (!el) return;
    setContainerWidth(el.getBoundingClientRect().width || DEFAULT_WIDTH);
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(() => setContainerWidth(el.getBoundingClientRect().width || DEFAULT_WIDTH));
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  // Drawn at the panel's real pixel width (not scaled from a fixed viewBox), so the text stays 11px at any size.
  const W = Math.max(280, Math.round(containerWidth));
  const n = points.length;
  const totals = points.map((p) => series.reduce((sum, s) => sum + (p.values[s.key] ?? 0), 0));
  const { top, ticks } = niceScale(Math.max(0, ...totals));
  const hasData = totals.some((t) => t > 0);

  const plotW = W - M.left - M.right;
  const plotH = H - M.top - M.bottom;
  const band = n > 0 ? plotW / n : plotW;
  const barW = Math.max(1, Math.min(MAX_BAR, band - GAP, band * 0.7));
  const yOf = (value: number) => M.top + plotH - (value / top) * plotH;
  // About 8 labels, anchored so the NEWEST day is always labelled (it is usually the one people look for).
  const labelEvery = Math.max(1, Math.ceil(n / Math.max(3, Math.floor(plotW / 70))));

  const showLegend = series.length > 1;
  const activePoint = active === null ? null : points[active];
  const activeCenter = active === null ? 0 : M.left + active * band + band / 2;
  const tooltipOnLeft = activeCenter > W / 2;

  return (
    <div ref={wrapRef} style={{ position: "relative" }}>
      {showLegend && (
        <div style={{ display: "flex", gap: 16, flexWrap: "wrap", marginBottom: 6, fontSize: 12, color: "var(--color-text-muted)" }}>
          {series.map((s) => (
            <span key={s.key} style={{ display: "inline-flex", alignItems: "center", gap: 6 }}>
              <span aria-hidden="true" style={{ width: 10, height: 10, borderRadius: 2, background: s.color, display: "inline-block" }} />
              {s.label}
            </span>
          ))}
        </div>
      )}

      <svg viewBox={`0 0 ${W} ${H}`} width={W} height={H} role="img" aria-label={ariaLabel} style={{ display: "block", overflow: "visible", maxWidth: "100%" }}>
        {ticks.map((t) => (
          <g key={t}>
            <line x1={M.left} x2={W - M.right} y1={yOf(t)} y2={yOf(t)} stroke="var(--color-border)" strokeWidth={1} opacity={t === 0 ? 1 : 0.7} />
            <text x={M.left - 8} y={yOf(t)} textAnchor="end" dominantBaseline="middle" fontSize={11} fill="var(--color-text-muted)">
              {formatTick(t)}
            </text>
          </g>
        ))}

        {points.map((p, i) => {
          const x = M.left + i * band + (band - barW) / 2;
          let running = 0;
          const lastNonZero = [...series].reverse().find((s) => (p.values[s.key] ?? 0) > 0)?.key;
          return (
            <g key={p.id}>
              {active === i && <rect x={M.left + i * band} y={M.top} width={band} height={plotH} fill="var(--color-surface-alt)" />}
              {series.map((s) => {
                const value = p.values[s.key] ?? 0;
                if (value <= 0) return null;
                const yTop = yOf(running + value);
                const yBottom = yOf(running) - (running > 0 ? GAP : 0);
                running += value;
                const height = Math.max(1.5, yBottom - yTop);
                return <path key={s.key} d={roundedTop(x, yBottom - height, barW, height, s.key === lastNonZero ? 4 : 0)} style={{ fill: s.color }} />;
              })}
              {(n - 1 - i) % labelEvery === 0 && (
                <text x={M.left + i * band + band / 2} y={H - 8} textAnchor="middle" fontSize={11} fill="var(--color-text-muted)">
                  {p.axisLabel}
                </text>
              )}
            </g>
          );
        })}

        {/* Hit targets: a full-height band per column, much bigger than the bar itself. */}
        {points.map((p, i) => (
          <rect
            key={`hit-${p.id}`}
            x={M.left + i * band}
            y={M.top}
            width={band}
            height={plotH}
            fill="transparent"
            tabIndex={0}
            role="img"
            aria-label={`${p.title}: ${series.map((s) => `${s.label} ${formatValue(p.values[s.key] ?? 0)}`).join(", ")}`}
            style={{ outline: "none", cursor: "default" }}
            onPointerEnter={() => setActive(i)}
            onPointerMove={() => setActive(i)}
            onPointerLeave={() => setActive((cur) => (cur === i ? null : cur))}
            onFocus={() => setActive(i)}
            onBlur={() => setActive((cur) => (cur === i ? null : cur))}
          />
        ))}

        {!hasData && (
          <text x={M.left + plotW / 2} y={M.top + plotH / 2} textAnchor="middle" fontSize={13} fill="var(--color-text-muted)">
            {emptyMessage ?? "No data in this range"}
          </text>
        )}
      </svg>

      {activePoint && (
        <div
          role="status"
          style={{
            position: "absolute",
            top: showLegend ? 30 : 8,
            // Anchored by the edge nearest the column, so the box gets the free space on its own side
            // (anchoring the far edge would squeeze it into the sliver of room beside the column).
            ...(tooltipOnLeft ? { right: W - (activeCenter - band / 2) + 8 } : { left: activeCenter + band / 2 + 8 }),
            width: "max-content",
            maxWidth: Math.max(160, Math.min(300, W - 40)),
            whiteSpace: "nowrap",
            pointerEvents: "none",
            zIndex: 5,
            minWidth: 150,
            padding: "8px 10px",
            borderRadius: 6,
            background: "var(--color-surface)",
            border: "1px solid var(--color-border)",
            boxShadow: "var(--shadow-card)",
            fontSize: 12,
          }}
        >
          <div style={{ color: "var(--color-text-muted)", marginBottom: 4 }}>{activePoint.title}</div>
          {series.map((s) => (
            <div key={s.key} style={{ display: "flex", alignItems: "center", gap: 8 }}>
              <span aria-hidden="true" style={{ width: 12, height: 3, borderRadius: 2, background: s.color, display: "inline-block" }} />
              <strong style={{ color: "var(--color-text)", fontSize: 13 }}>{formatValue(activePoint.values[s.key] ?? 0)}</strong>
              <span style={{ color: "var(--color-text-muted)" }}>{s.label}</span>
            </div>
          ))}
          {activePoint.details?.map((d) => (
            <div key={d} style={{ color: "var(--color-text-muted)", marginTop: 2 }}>{d}</div>
          ))}
        </div>
      )}
    </div>
  );
}
