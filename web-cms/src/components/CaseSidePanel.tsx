import { Link } from "react-router-dom";
import CaseDetailView from "./CaseDetailView";

interface CaseSidePanelProps {
  caseId: string;
  onClose: () => void;
  onChanged?: () => void;
}

/** Right-hand Case panel: stays in view while the list scrolls, with its own scroll. */
export default function CaseSidePanel({ caseId, onClose, onChanged }: CaseSidePanelProps) {
  return (
    <aside style={panelStyle}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: 12, gap: 8 }}>
        <strong>Case detail</strong>
        <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
          <Link to={`/cases/${caseId}`} style={{ fontSize: 13 }}>Full page</Link>
          <button onClick={onClose} aria-label="Close case detail" title="Close">✕</button>
        </div>
      </div>
      <CaseDetailView key={caseId} caseId={caseId} compact onChanged={onChanged} />
    </aside>
  );
}

const panelStyle: React.CSSProperties = {
  width: 520,
  flexShrink: 0,
  borderLeft: "1px solid var(--color-border)",
  paddingLeft: 24,
  position: "sticky",
  top: 16,
  alignSelf: "flex-start",
  maxHeight: "calc(100vh - 110px)",
  overflowY: "auto",
};
