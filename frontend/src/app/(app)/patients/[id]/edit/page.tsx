import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { EditPatientPage } from "@/features/patients/patient-pages";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("patients");
  return { title: t("edit") };
}

export default function Page() {
  return <EditPatientPage />;
}
