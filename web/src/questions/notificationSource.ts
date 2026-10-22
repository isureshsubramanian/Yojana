import type { ShellNotificationSource } from "../shell/notificationSource";
import { questionsApi } from "./api";

export const questionNotificationSource: ShellNotificationSource = {
  key: "questions",
  async load(signal) {
    const summary = await questionsApi.bell(signal);
    return { count: summary.count, items: summary.items.map(item => ({
      id: item.id, title: item.title, detail: `${item.reason} · ${item.recordName}`,
      href: item.link, at: item.activityAt,
    })) };
  },
};
