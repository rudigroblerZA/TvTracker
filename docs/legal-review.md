# Legal review of the privacy policy and terms

14 November 2026. This is a review by the software's author against the app's own code and the main requirements of
the GDPR (EU and UK), South Africa's POPIA, ECT Act and Consumer Protection Act, and EU consumer law. It is not
legal advice, and nobody qualified to practise law has read it. Its purpose is to make the two pages accurate about
what the app does, complete enough to launch a beta on, and to hand a lawyer a short list of what is left for them.

The two pages are `src/Clients/TvTracker.UI/Pages/PrivacyPage.razor` (`/privacy`) and `TermsPage.razor` (`/terms`).

## What was wrong or missing, and what was done

| # | Finding | Why it matters | Done |
| --- | --- | --- | --- |
| 1 | The policy named no legal basis for each use of data | GDPR art. 13 requires it; POPIA s. 18 requires the purpose | A table of purposes and what allows each |
| 2 | Technical data wasn't mentioned: network addresses in rate limits and server logs | It is personal data | Listed under what we keep, with why |
| 3 | Processors were incomplete: only Stripe and a general "hosting providers" | Recipients must be named or described | Hosting, payment, email delivery and push services each described |
| 4 | Nothing on data leaving the user's country | Required where it happens (GDPR ch. V, POPIA s. 72) | A section, in general terms (see lawyer item 3) |
| 5 | Retention didn't cover backups, logs or sign-in sessions | Backups hold a deleted account for up to two weeks | Each stated; container logs are now capped so "a limited time" is true |
| 6 | No word on security or on telling people about a breach | Expected by both laws | A short section |
| 7 | No named complaint route | GDPR art. 13(2)(d); POPIA s. 18(1)(h) | EU authorities, the UK ICO and South Africa's Information Regulator named |
| 8 | "Push and email are off until you turn them on" would have been untrue: push is on once the device grants permission | A policy must match the product | Worded as it works |
| 9 | The operator's address and registration weren't shown | ECT Act s. 43 and EU rules require a seller to identify itself | New settings (below); shown on both pages when set |
| 10 | Automated recommendations weren't addressed | GDPR art. 22 disclosure | Stated that they decide nothing with legal effect |
| 11 | The terms gave no way to change one's mind after buying | EU and UK consumers have 14 days for online purchases, and the checkout doesn't collect the consent that would waive it; the ECT Act gives 7 days | A 14-day refund on a first purchase, for everyone |
| 12 | Automatic renewal wasn't prominent | Consumer law in several places requires it to be clear | Stated in bold |
| 13 | The liability clause excluded too much for consumers | Exclusions of negligence causing injury, fraud and gross negligence are void in the UK, EU and under the CPA | Carve-outs added; cap kept with a floor |
| 14 | No governing law could be stated | Depends on where the operator is | A setting; consumers keep their own country's protection and courts |
| 15 | No route to report unlawful or infringing content | Hosts need one to rely on safe harbours | Flag in the app, or email |
| 16 | Nothing on closing the service, transfer, or the app stores | Standard terms | Added, with refunds of unused paid time |
| 17 | "Where to watch" read as fact | It is inferred from the data source, not licensed availability | The terms now say so |
| 18 | Minors could buy a plan | Contracts with minors are voidable | A parent or guardian must agree and buy |

## Settings the pages need

| Setting (`.env` in a deployment) | Shown as | Needed by |
| --- | --- | --- |
| `OPERATOR_NAME` | Who runs the app | Both pages (already required) |
| `CONTACT_EMAIL` | Where to write | Both pages (already required) |
| `OPERATOR_ADDRESS` | The operator's postal address | ECT Act s. 43; EU consumer law |
| `COMPANY_NUMBER` | A phrase such as "registered in South Africa, no. 2026/123456/07" | The same |
| `GOVERNING_LAW` | Whose law the terms are under, e.g. "South Africa" | The terms; without it they say "the place where the operator is established" |

Locally they are `Branding:OperatorAddress`, `Branding:CompanyNumber` and `Branding:GoverningLaw`.

## Commitments the pages now make

These are promises to users. Each is a business decision the owner can change, but the app or its operator must
keep whichever is published.

- A full refund within 14 days of a first purchase, on request. Refunds are made by hand in the payment provider's
  dashboard; the app doesn't yet act on a refund (the plan runs to the end of its period).
- 30 days' notice of a price change, of a change to the terms that affects users, and of closing the service.
- A refund of the unused part of a paid plan if the service closes, if an account is closed without a breach, or if a
  user leaves because of a change to the terms.
- An answer to a data request within a month.
- Telling users and the regulator about a breach that puts their data at risk.

## What the app does that the pages rely on

Checked in the code on the date above. If one of these changes, the page must.

- Export and deletion reach every service that holds user data; deletion removes the data before the login.
- Notifications are removed 90 days after being read and after a year at most.
- Affiliate click records hold the show, service, region, link and plan, and no user id, address or browser.
- Figures about other users need five or more people or ratings.
- Email and the weekly digest are off by default; push is on but needs the device's permission.
- The sign-in cookie lasts seven days, sliding. The only cookies are sign-in, antiforgery and the sign-in service's.
- Backups are kept 14 days by default (`BACKUP_KEEP_DAYS`). If that is raised, the policy's "about two weeks" must be.
- Receipts are deleted with the account (see lawyer item 5).

## For a lawyer to confirm

1. **Where the operator is established, and so which law applies.** Everything below depends on it. The pages are
   written to work under South African, EU and UK law, without having been checked by a practitioner in any.
2. **POPIA formalities** if the operator is South African: registering an Information Officer with the Information
   Regulator, and a PAIA manual. Neither is a matter of wording on these pages.
3. **Transfers abroad.** The policy says a recognised safeguard is relied on. That is only true once there are
   data processing agreements with the hosting, payment and email providers that include one (standard contractual
   clauses, or the UK addendum). The provider and the country the servers are in should then be named.
4. **An EU and a UK representative** (GDPR art. 27). An operator outside the EU or UK that offers a paid service to
   people there generally needs one in each. The policy names none.
5. **Receipts on deletion.** The app deletes its copy of a user's receipts when the account is deleted and relies
   on the payment provider's records. Whether that satisfies the operator's own record-keeping duty for tax (five
   years in South Africa) is for an accountant; if not, receipts should be kept, detached from the account, and
   the policy changed to say so.
6. **Tax.** No VAT or sales tax is collected at checkout, by decision. The terms say the price is the one shown at
   checkout and nothing about tax. Selling to EU consumers creates a VAT obligation from the first sale.
7. **Acceptance at sign-up.** The terms say that creating an account accepts them, but the registration form (the
   sign-in service's) doesn't show them or ask for agreement. A link and a tick box there is the stronger position,
   and proof of age or of a parent's consent is not collected at all.
8. **The liability cap and the 14-day refund**, for enforceability and for whether the owner wants to offer the
   refund to everyone rather than only where the law requires it.
9. **TVmaze's licence.** Its data is CC BY-SA, which may require the operator to share its adapted catalog under the
   same licence, and its pictures are loaded from TVmaze's servers. Both need TVmaze's terms read for a paid service.
10. **The app stores.** Apple requires its own minimum terms in an app's licence agreement, and both stores require
    in-app purchase for subscriptions sold in the app. The terms mention the stores only in passing.
11. **Marketing email.** None is sent today. If it ever is, it needs its own consent and an unsubscribe link; the
    weekly digest has neither a one-click unsubscribe link nor a `List-Unsubscribe` header yet.
12. **Affiliate disclosure.** The terms and the policy disclose affiliate links. Regulators in the US and UK expect
    the disclosure beside the link itself, which the show page doesn't have.

## Not covered

Employment, company formation, trademarks (the name "TV Tracker" has not been searched), and the laws of any country
other than those named above.
