import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { RouterProvider } from "react-router";
import "./styles/theme-glass.css";
import "./styles/base.css";
import { IconSprite } from "./shell/Icons";
import { router } from "./routes";

const root = document.getElementById("root");
if (!root) throw new Error("#root missing");
createRoot(root).render(
  <StrictMode>
    <IconSprite />
    <RouterProvider router={router} />
  </StrictMode>,
);
