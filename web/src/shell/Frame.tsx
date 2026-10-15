import { useEffect } from "react";
import { Outlet, useNavigate, useNavigation } from "react-router";
import { TopBar } from "./TopBar";
import { SideNav } from "./SideNav";
import { installShortcuts } from "./shortcuts";
import { SearchPalette } from "../components/search/SearchPalette";

/** The signed-in frame: top bar, navigation, content and (per page) a right rail, on the 56/220/272 grid of the mockup. */
export function Frame() {
  const navigate = useNavigate();
  const navigation = useNavigation();
  useEffect(() => installShortcuts(navigate), [navigate]);
  return (
    <>
      {navigation.state !== "idle" && <div className="loading-bar" role="progressbar" aria-label="Loading" />}
      <a className="skip" href="#main">Skip to content</a>
      <div className="frame" id="frame">
        <TopBar />
        <SideNav />
        <Outlet />
      </div>
      <SearchPalette />
    </>
  );
}

/** The frame without navigation: sign-in and tenant selection. */
export function BareFrame({ children }: { children: React.ReactNode }) {
  return (
    <div className="frame bare">
      <TopBar bare />
      <main className="content center" id="main" tabIndex={-1}>{children}</main>
    </div>
  );
}
