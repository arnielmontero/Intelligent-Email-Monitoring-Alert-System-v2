interface PaginationProps {
  page: number;
  totalPages: number;
  totalCount: number;
  pageSize: number;
  onChange: (page: number) => void;
}

/** Numbered pages with first/last always shown and gaps collapsed, e.g. 1 … 4 5 6 … 20. */
function pageNumbers(page: number, totalPages: number): Array<number | "gap"> {
  const pages = new Set([1, totalPages, page - 1, page, page + 1].filter((p) => p >= 1 && p <= totalPages));
  const sorted = Array.from(pages).sort((a, b) => a - b);
  const result: Array<number | "gap"> = [];
  sorted.forEach((p, i) => {
    if (i > 0 && p - sorted[i - 1] > 1) result.push("gap");
    result.push(p);
  });
  return result;
}

export default function Pagination({ page, totalPages, totalCount, pageSize, onChange }: PaginationProps) {
  const from = totalCount === 0 ? 0 : (page - 1) * pageSize + 1;
  const to = Math.min(page * pageSize, totalCount);

  return (
    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12, flexWrap: "wrap", marginTop: 12 }}>
      <span style={{ color: "var(--color-text-muted)", fontSize: 13 }}>
        Showing {from}–{to} of {totalCount}
      </span>
      <div style={{ display: "flex", gap: 4, alignItems: "center" }}>
        <button disabled={page <= 1} onClick={() => onChange(page - 1)}>‹ Prev</button>
        {pageNumbers(page, totalPages).map((p, i) =>
          p === "gap" ? (
            <span key={`gap-${i}`} style={{ padding: "0 4px", color: "var(--color-text-muted)" }}>…</span>
          ) : (
            <button key={p} className={p === page ? "btn-primary" : undefined} onClick={() => onChange(p)} aria-current={p === page}>
              {p}
            </button>
          ),
        )}
        <button disabled={page >= totalPages} onClick={() => onChange(page + 1)}>Next ›</button>
      </div>
    </div>
  );
}
