import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PatientDetailsPage } from "@/features/patients/patient-pages";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("patients");
  return { title: t("details") };
}

export default function Page() {
  return <PatientDetailsPage />;
}
