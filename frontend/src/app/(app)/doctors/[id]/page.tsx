import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { DoctorPage } from "@/features/doctors/doctor-pages";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("doctors");
  return { title: t("details") };
}

export default function Page() {
  return <DoctorPage />;
}
