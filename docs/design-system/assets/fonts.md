# Fonts

This design system uses three font families, all available from Google Fonts. Load them as early as possible in the document head.

## Direct CSS load (recommended for most apps)

```html
<link rel="preconnect" href="https://fonts.googleapis.com"/>
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin/>
<link
  href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=Bricolage+Grotesque:wght@500;600;700;800&family=JetBrains+Mono:wght@400;500;600;700&display=swap"
  rel="stylesheet"
/>
```

## Next.js (App Router) via `next/font/google`

```ts
// app/layout.tsx
import { Inter, Bricolage_Grotesque, JetBrains_Mono } from "next/font/google";

const inter = Inter({
  subsets: ["latin"],
  weight: ["400","500","600","700"],
  variable: "--font-sans",
  display: "swap",
});
const bricolage = Bricolage_Grotesque({
  subsets: ["latin"],
  weight: ["500","600","700","800"],
  variable: "--font-display",
  display: "swap",
});
const jetbrains = JetBrains_Mono({
  subsets: ["latin"],
  weight: ["400","500","600","700"],
  variable: "--font-mono",
  display: "swap",
});

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" className={`${inter.variable} ${bricolage.variable} ${jetbrains.variable}`}>
      <body>{children}</body>
    </html>
  );
}
```

Then in `tokens.css` or your global CSS:
```css
:root {
  --font-sans:    var(--font-sans, "Inter"), system-ui, sans-serif;
  --font-display: var(--font-display, "Bricolage Grotesque"), var(--font-sans);
  --font-mono:    var(--font-mono, "JetBrains Mono"), ui-monospace, monospace;
}
```

## Self-hosted (if you need offline / air-gapped)

Download the WOFF2 files from Google Fonts (use Bunny Fonts mirror or the `google-webfonts-helper` tool), drop them into `public/fonts/`, and update `tokens.css`:

```css
@font-face {
  font-family: "Inter";
  font-style: normal;
  font-weight: 400 700;
  font-display: swap;
  src: url("/fonts/inter-var.woff2") format("woff2");
}
/* repeat for Bricolage Grotesque and JetBrains Mono */
```

## Weights used by the design system

| Family | Weights |
|---|---|
| Inter | 400, 500, 600, 700 |
| Bricolage Grotesque | 500, 600, 700, 800 |
| JetBrains Mono | 400, 500, 600, 700 |

Don't load weights we don't use. If you absolutely need italics anywhere, only Inter italic is allowed and only in marketing copy — never in product UI.
