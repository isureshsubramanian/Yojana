import { Link, isRouteErrorResponse, useRouteError } from "react-router";
import { ApiError } from "../api/client";

export function ErrorPage() {
  const error = useRouteError();
  let title = "Something went wrong";
  let detail = "The page could not be shown. Try again, or go back to the overview.";
  if (isRouteErrorResponse(error)) {
    title = error.status === 404 ? "Page not found" : `Error ${error.status}`;
    detail = error.status === 404 ? "There is nothing at this address." : error.statusText || detail;
  } else if (error instanceof ApiError) {
    title = error.status === 403 ? "Not permitted" : `Request failed (${error.status})`;
    detail = error.status === 403 ? "Your roles in this tenant do not include this area." : error.message;
  }
  return (
    <main className="content center" id="main" tabIndex={-1}>
      <div className="auth">
        <section className="card">
          <div className="empty">
            <h2>{title}</h2>
            <p className="hint">{detail}</p>
            <p><Link className="btn" to="/">Back to overview</Link></p>
          </div>
        </section>
      </div>
    </main>
  );
}
