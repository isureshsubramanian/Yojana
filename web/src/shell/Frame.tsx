import { useEffect } from "react";
import { Outlet, useNavigate, useNavigation } from "react-router";
import { TopBar } from "./TopBar";
import { SideNav } from "./SideNav";
import { installShortcuts } from "./shortcuts";
import { SearchPalette } from "../components/search/SearchPalette";
import { Drawer } from "./Drawer";
import { useDrawer } from "./drawerStore";
import { questionNotificationSource } from "../questions/notificationSource";

const notificationSources = [questionNotificationSource];

/** The signed-in frame (house layout E): top bar, icon navigation, full-width content and the drawer the pages feed. */
export function Frame() {
  const navigate = useNavigate();
  const navigation = useNavigation();
  const drawer = useDrawer();
  useEffect(() => installShortcuts(navigate), [navigate]);
  return (
    <>
      {navigation.state !== "idle" && <div className="loading-bar" role="progressbar" aria-label="Loading" />}
      <a className="skip" href="#main">Skip to content</a>
      <div className={`frame${drawer.open ? " drawer-open" : ""}`} id="frame">
        <TopBar notificationSources={notificationSources} />
        <SideNav />
        <Outlet />
        <Drawer />
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
