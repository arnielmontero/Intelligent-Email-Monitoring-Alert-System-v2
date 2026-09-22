interface PlaceholderPageProps {
  title: string;
}

export default function PlaceholderPage({ title }: PlaceholderPageProps) {
  return (
    <div>
      <h1>{title}</h1>
      <p>This module has not been implemented yet. See IEMAS_Build_Progress_Tracker.md for status.</p>
    </div>
  );
}
