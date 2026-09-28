import { Link, useParams } from "react-router-dom";
import CaseDetailView from "../../components/CaseDetailView";

export default function CaseDetailPage() {
  const { id } = useParams<{ id: string }>();
  if (!id) return null;
  return (
    <div>
      <div style={{ color: "var(--color-text-muted)", fontSize: 13, marginBottom: 8 }}>
        <Link to="/cases">Cases</Link> · <Link to="/case-history">Case History &amp; Logs</Link>
      </div>
      <CaseDetailView caseId={id} />
    </div>
  );
}
