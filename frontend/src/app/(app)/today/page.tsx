import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { TodayPage } from "@/features/today/today-page";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("today");
  return { title: t("title") };
}

export default function Page() {
  return <TodayPage />;
}
