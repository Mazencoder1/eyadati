import { redirect } from "next/navigation";
import { HOME_PATH } from "@/lib/routes";

/** The app's home is the role-based "Today" page; the shell redirects to /login when signed out. */
export default function Home() {
  redirect(HOME_PATH);
}
