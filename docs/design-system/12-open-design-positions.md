# Open design positions — landed 2026-08-06

Four positions came back from Claude Design against the briefs in the frontend repo's
`docs/design-briefs-2026-08-02.md`. They are **positions, not built work** — read the caveats
before treating any of them as a spec.

| Position | File | State |
|---|---|---|
| Desktop tap targets (WP-31b) | `desktop-tap-targets.md` | **Blocked on a visual review** — see below |
| SFTP host-key trust UI | `SFTP-host-key-trust.md` | Ready to build; backend verified |
| `/pricing` + `/security` rebalance | `pricing-security-rebalance.md` | Specified, **not built**; claims need verifying one by one |
| Practice-order ending | `practice-order-ending.md` | Ready to build; needs an echo endpoint |

---

## Two things that will mislead you if nobody says them

### 1. A second, conflicting tap-target position exists and was deliberately not landed

The drop contained **two** files with different numbers for the same rule:

| | `desktop-tap-targets.md` (landed) | `WP31b-desktop-tap-targets.md` (not landed) |
|---|---|---|
| Committing | 40×40 (44 coarse) | **44×44** |
| Middle tier | Operating — 32 visible / 48 hit | Navigating — 32 visible / **44** hit |
| Smallest tier | Incidental — 24 hard floor | Inline — 24 visible / **32 hit** |
| Smallest legal control @13px | **28px** | 24px text / 28px labelled |
| Inputs | **36px** min | 32 compact / 40 default |
| Tokens | `--pl-target-commit/operate/min/gap`, `--pl-control-h-sm`, `--pl-input-h` | `--pl-target-commit/nav/inline`, `--pl-hit-pad` |

The landed file is the one the founder summarised as the position, so it is authoritative. The
other is an earlier draft. **Landing both would have put two contradictory specs in the design
system**, which is how the next session picks the wrong one and nobody notices until the tokens
disagree with the components.

If the 44px numbers turn out to be the intended ones, replace the landed file rather than adding
the draft beside it.

**Known internal inconsistency in the landed file:** its tier table says Operating is "32×32
visible, **40×40** hit", while §2 of the same document derives "32px visible → **48px** hit" from
`inset:-8px`. 8px of inset on each side of a 32px box is 48px, so §2's arithmetic is right and the
table's 40 is wrong. Use 48. Fix the table when the position is implemented.

### 2. The tap-target sweep is gated on a visual review that has not happened

Both variants say the same thing and it is the reason nothing was swept: **before/after captures of
`/settings` and `/operations/webhooks` at 1280×900 are outstanding.** The density cost of raising
~50 controls has to be judged by looking at it, not asserted. Do not implement the sweep until
those captures exist and someone has looked at them — a density regression across the working
surfaces is exactly the failure the rule was written to avoid.

## What is verified, and what is not

**Verified against the backend on 2026-08-06** (`pricing-security-rebalance.md` §2 proposes claiming
"we verify the supplier's server identity and refuse to deliver if it changes"):

- It is **true for SFTP**. `SftpDeliveryDispatcher.cs:158` builds the verifier;
  `SshHostKeyVerifier.cs:97` calls `SshHostKeyPolicy.Decide`, `:99` denies on `Rejected`, `:101`
  records the rejection, `:117` throws it. `SshHostKeyPolicy.cs:97` learns on first use, `:101`
  treats a blank observation as `Rejected` (fail-safe, and the comment says why), `:107` compares
  ordinally. The same policy guards SFTP ingress and catalog pull.
- **It is SFTP only.** There is no equivalent control on HTTP/webhook, email, or the ERP adapters,
  and the capability ledger's unknown **U-3** records that *no* delivery channel requires TLS —
  `http://` is permitted everywhere. So an unqualified "we verify the supplier's server identity"
  on `/security` would be **false for every HTTP supplier**, which is most of them. Scope the claim
  to the channel or leave it out.

**Not verified — do not ship these without checking each one:** "every delivery is provable"
(plausible; WP-34 landed artifact name + fingerprint per attempt), "nothing sends until it
validates" (WP-17 landed a server-side acceptance gate — check whether an override exists, because
if one does then "cannot be sent" is the wrong word), and "credentials are never shown after entry;
secrets are masked and rotatable" (**completely unchecked**).

The whole point of the `/pricing` + `/security` rebalance is to replace withdrawn claims with true
ones. Replacing them with unverified ones would repeat the defect it exists to fix.

## Not landed from the same drop

The archive also re-exported the core design system. `02-tokens.md`, `04-color.md`,
`05-components.md`, `10-claude-code-brief.md` and `README.md` differ from the repo's copies, and the
export **lacks** `00-agent-quick-brief.md` and `11-unified-page-rules.md` entirely. Copying it
wholesale would have reverted curated edits and dropped two files, so none of it was taken. If a
fuller sync is wanted, diff those five files individually and decide per file.

`COLORS_AND_FONTS.md`, `DESIGN_SYSTEM.md`, `FABLE5_BRIEF.md`, `core.jsx`, `styleguide.*`,
`shadcn-theme.css` and `tailwind.preset.ts` were also in the drop and are not landed — the repo's
tokens live in `tokens/` and `tailwind.config.ts`, and a second source of truth for tokens is worse
than none.
