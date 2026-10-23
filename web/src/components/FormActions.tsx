/**
 * The commit buttons of a settings form, placed directly under its last field rather than in the page header, so the
 * pointer travels the least. Sticks to the bottom of the window while a long form scrolls.
 */
export function FormActions({ children, status }: { children: React.ReactNode; status?: React.ReactNode }) {
  return <div className="form-actions">{children}{status && <span className="form-actions-status" role="status">{status}</span>}</div>;
}
