/**
 * A short, human label for a session's User-Agent ("Chrome · Windows"). Only for display: the server stores the raw
 * header, and an unknown or missing value simply falls back to the generic label.
 */
export function describeDevice(userAgent: string | null | undefined): { browser: string | null; os: string | null } {
  const ua = userAgent ?? "";
  // Order matters: Edge and Opera also say "Chrome"; Chrome also says "Safari".
  const browser = /Edg\//.test(ua)
    ? "Edge"
    : /OPR\//.test(ua)
      ? "Opera"
      : /Firefox\//.test(ua)
        ? "Firefox"
        : /Chrome\//.test(ua)
          ? "Chrome"
          : /Safari\//.test(ua) && /Version\//.test(ua)
            ? "Safari"
            : null;
  const os = /iPhone|iPad|iPod/.test(ua)
    ? "iOS"
    : /Android/.test(ua)
      ? "Android"
      : /Windows/.test(ua)
        ? "Windows"
        : /Mac OS X|Macintosh/.test(ua)
          ? "macOS"
          : /Linux/.test(ua)
            ? "Linux"
            : null;
  return { browser, os };
}
