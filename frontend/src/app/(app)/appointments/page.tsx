import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { AppointmentsBoard } from "@/features/appointments/appointments-board";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("appointments");
  return { title: t("title") };
}

export default function Page() {
  return <AppointmentsBoard />;
}
