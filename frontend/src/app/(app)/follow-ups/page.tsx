import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { FollowUpsPage } from "@/features/follow-ups/follow-ups-page";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("followUps");
  return { title: t("title") };
}

export default function Page() {
  return <FollowUpsPage />;
}
