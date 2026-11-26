# Is TvTracker feasible as a paid product?

26 November 2026. Research by an agent against the repository's own code and the providers' own published terms,
pricing pages and official guidance, all read on 26 November 2026 unless a line says otherwise. It is not legal, tax
or accounting advice. Terms and prices change: each claim carries the page it came from, and the last two sections
list what needs the owner or a lawyer and what could not be verified.

How to read it: **Verified** means the page was fetched and says this. **Inference** means it is my reading of
verified facts. **Unverified** means the page could not be reached or did not say, and anything given is labelled as
from memory or from a secondary source.

## Verdict

**Feasible with conditions.** Nothing found makes a paid TvTracker unlawful or impossible as built, and the running
cost is low enough that a few dozen subscribers cover it. Three things decide it:

1. **Data rights are fine for the default source and not for the optional ones.** TVmaze's licence allows any use
   with credit, but it is share-alike. TMDB forbids use in an app that charges, without a written agreement.
   JustWatch is contract-only at an unpublished price, and its public site forbids scraping and commercial use.
   TheTVDB is free under $50k a year of revenue.
2. **Tax on sales is not handled on the Stripe path.** The code collects no VAT or sales tax, and EU and UK VAT is
   due from the first sale to a consumer there. Selling through a merchant of record (Polar, which the code already
   supports, or Stripe Managed Payments) closes this. Selling directly through Stripe means registering and filing.
3. **The margin per subscriber is thin and the income is almost all subscriptions.** At $2.99 a month a fixed
   per-payment fee takes 14% to 24%. Hosting needs roughly 18 to 50 paying members at 1,000 members. Affiliate
   income is small, slow to start and, on JustWatch rows, not the app's at all.

The mobile app is already built the way the stores allow without commission (it sells nothing in the app). That
should stay until there is a reason to pay 15% for in-app sales.

## What the code depends on

Established from the repository, not from the web.

| Dependency | Used for | Where in the code | Required? |
| --- | --- | --- | --- |
| TVmaze API (`api.tvmaze.com`) | The catalog: shows, episodes, cast, crew, ratings, images (hot-linked), and the where-to-watch guess from its network and web-channel fields | `src/Services/Catalog/Catalog.SyncWorker/Providers/TvMazeCatalogProvider.cs`, `TvMazeBudget.cs`; README "The catalog is filled from TVmaze" | Yes, by default (`Catalog:Provider` unset) |
| TheTVDB v4 API | An alternative catalog | `Catalog.SyncWorker/Providers/Tvdb/`; `AppHost.cs` (`tvdb-api-key`, `tvdb-pin`) | Only with `Catalog:Provider=tvdb` |
| Wikidata, Wikipedia | An alternative catalog; gap-fill, the TV atlas, awards and Research's comparison | `Catalog.SyncWorker/Providers/Wikidata/`, `Supplement/`; Research.API | Optional jobs; or the catalog with `Catalog:Provider=wikidata` |
| Wikiquote | "Memorable lines" | `Catalog.SyncWorker/Quotes/WikiquoteClient.cs` | Optional feature |
| JustWatch Partner API (`apis.justwatch.com/contentpartner/`) | Where to watch per country | `Catalog.SyncWorker/JustWatch/JustWatchClient.cs`; `AppHost.cs` (`justwatch-token`) | Off until a token is set. The code calls only the partner API with a token; there is no scraping of justwatch.com in the repository |
| TMDB API | A source for moderators' Research | `AppHost.cs` (`tmdb-api-key`), Research.API | Off until a key is set; `research` flag is off by default |
| GIPHY API | GIFs in comments | `AppHost.cs` (`giphy-api-key`), Ratings.API | Off until a key is set |
| Stripe, Polar | Selling plans | `src/Services/Financials/Financials.API/Payments/StripePaymentProvider.cs`, `PolarPaymentProvider.cs`, `PaymentOptions.cs` | One of them, to sell |
| Affiliate networks | Tracked where-to-watch links and postbacks | `Financials.API/Services/Affiliates.cs`, `Apis/AffiliateApi.cs`; README "Affiliate links, measured" | Optional (`affiliates` flag) |
| Azure Communication Services | The app's own email (alerts, weekly digest) | `AppHost.cs` (`email-connection-string`) | Optional; none sent without it |
| Any SMTP service | Keycloak's mail (password reset, address confirmation) | `AppHost.cs` (`smtp-*`) | Needed in practice |
| Azure Notification Hubs with FCM/APNs; VAPID keys | Phone and browser push | README "Configuration still to do" | Not configured yet |
| Google sign-in | External login | `AppHost.cs` (`google-client-id`) | Optional |

**Plans and prices.** Free, Pro (key `plus`) and Family (`src/TvTracker.IntegrationEvents/Plans.cs`). Family is Pro
for the owner and up to 5 invited people (`Plans.FamilyMembers`). Free has no list limit; the only limit in
`PlanLimits` that differs is `TrackServices` (Pro and Family only). What Pro sells is 17 insight features gated by
flags (README "Plans"). Default prices are in `PaymentOptions.cs` (`PlanPrices.Default`): **Pro $2.99 a month or
$29.99 a year; Family $4.99 or $49.99**, in USD, with per-region overrides in `Payments:Prices`. The fee estimate in
the books defaults to 2.9% + $0.30 (`Financials.API/Services/FinancialStatement.cs`, `FinancialOptions`).

**Infrastructure.** `src/TvTracker.AppHost/AppHost.cs` publishes to one machine running Docker Compose
(`AddDockerComposeEnvironment`): Postgres with pgvector holding ten service databases plus Keycloak's, Redis,
RabbitMQ, Keycloak 26.4, flagd, Docker Model Runner serving an embedding model on the CPU, the .NET services and
workers, the mobile gateway, the web app, Caddy, a daily backup container and an rclone off-site container. The
README's "Deploying" section says: "About 8 GB of memory and 20 GB of disk is a sensible minimum: there are 21
containers, and the embedding model is about 640 MB." It also says one copy of each service is assumed ("Before
production").

**Mobile.** The Android app sells nothing: `IPlatformServices.SellsPlans` is false on the phone, and people buy on
the web (README "Releasing the Android app"). iOS has not been built.

## 1. Catalog and where-to-watch data licensing

| Source | Commercial, paid use | Attribution | Rate and caching rules | Cost |
| --- | --- | --- | --- | --- |
| TVmaze | Allowed. "Use of the TVmaze API is licensed by CC BY-SA"; the data "can freely be used for any purpose" if credited | A link back to TVmaze satisfies it | "at least 20 calls every 10 seconds per IP address"; responses are cached 60 minutes on their side; hot-linking images is allowed, caching them is recommended | Free. A paid arrangement exists only for needs beyond the public API ("Contact our sales department"); no price published |
| TheTVDB | Allowed with a key. "Our API is available for both commercial projects and to individual developers" | "attribution with a direct link to TheTVDB.com must be displayed to end users viewing metadata from our API" | None published on the pages read | By the company's yearly revenue: under $50k free with attribution; $50k to $250k $1,000 a year; $250k to $1M $10,000 a year; over $1M by negotiation |
| Wikidata | Allowed. Structured data is CC0 | Not required, requested | Descriptive User-Agent; back off on 429 and honour Retry-After; no heavy query-service use; dumps for bulk | Free |
| Wikipedia, Wikiquote text | Allowed under CC BY-SA 4.0; the licences "do allow commercial uses" | A hyperlink or URL to the page reused; say if changed | Wikimedia's User-Agent policy, robot policy and API etiquette are part of the terms; no "undue burden on an API" | Free |
| TMDB | **Not allowed without a contract.** The licence "does not permit any commercial use"; charging users for an app that integrates TMDB is given as an example | TMDB logo and a fixed notice | No caching "for longer than 6 months" | "Any such agreement may be subject to, among other things, payment of fees"; no price published |
| JustWatch Partner API | Only under a signed contract | "Each partner is required to integrate branded links to the JustWatch website", per country and title, anchor text "JustWatch" or its logo | None published | Not published; by enquiry |
| GIPHY | Not stated on the API page; production keys are by application and "a member of our team will reach out via email to discuss pricing" | "Powered By GIPHY", conspicuously | Beta keys: 100 calls an hour | Not published |

Sources: TVmaze API page, https://www.tvmaze.com/api. TheTVDB, https://thetvdb.com/api-information and
https://thetvdb.com/api-information/signup. Wikidata, https://www.wikidata.org/wiki/Wikidata:Data_access. Wikimedia
Terms of Use, https://foundation.wikimedia.org/wiki/Policy:Terms_of_Use. TMDB API terms,
https://www.themoviedb.org/api-terms-of-use and https://www.themoviedb.org/api-for-business. JustWatch Partner API
documentation, https://apis.justwatch.com/docs/api/. GIPHY, https://developers.giphy.com/docs/api/.

### TVmaze and share-alike

Verified: the CC BY-SA 4.0 legal code grants a "worldwide, royalty-free, non-sublicensable, non-exclusive,
irrevocable" licence with no non-commercial restriction, and section 4(b) says that when licensed database contents
are included in a database of your own, that database "(but not its individual contents) is Adapted Material"
(https://creativecommons.org/licenses/by-sa/4.0/legalcode.en). Section 3(b) requires adapted material that is shared
to carry the same licence.

Inference: charging for TvTracker is within TVmaze's licence. The open point is share-alike, already item 9 in
`docs/legal-review.md`. The catalog TvTracker shows the public (TVmaze's data with moderators' corrections and
Wikidata gap-fill merged in) is probably adapted material that must be offered under CC BY-SA. That does not reach
the source code, members' data or the insight rules, but the app's terms and `LICENSE` ("all rights reserved")
should say the catalog data is CC BY-SA rather than claim it. The insights computed from TVmaze ratings are the
part a lawyer should look at: whether a derived score is "adapted material" is not something the licence text
settles.

Rate limits are not a scaling problem. The sync worker throttles to TVmaze's limit, and calls grow with the catalog
and with new search terms, not with members (README, "Run it").

### TheTVDB

Verified: the signup page shows revenue tiers, as in the table. The README describes a "user-supported model, which
also needs a subscriber's PIN"; the pages read today do not mention it, so that model is unverified as current.
Inference: under $50k a year TheTVDB costs nothing and is a real fallback catalog if TVmaze becomes unavailable,
with fewer features (no ratings, so most rating-based insights go).

### TMDB

Verified, and it matters because Research can read TMDB: "If Your use (or intended use) is commercial, You must
enter into a written agreement with TMDB". Inference: on a deployment that charges, leave `tmdb-api-key` unset
unless there is an agreement. This costs little, since Research is a moderators' tool behind a flag that is off.

### JustWatch

Verified:

- There is a sanctioned route: the Partner API. "Once the contract is concluded, each partner is handed a unique
  partner token. This token is required for every request made to the JustWatch Partner API."
  (https://apis.justwatch.com/docs/api/). JustWatch's business page describes "A unified 'where to watch' API",
  "Easy to install widgets" and "Multiple revenue and commission opportunities", and directs enquiries to a contact
  form or data-partner@justwatch.com (https://www.justwatch.com/us/JustWatch-Streaming-API). No price, eligibility
  rule or self-serve sign-up is published.
- Unofficial use is prohibited. The Terms of Use, updated 5 May 2026, say the service "is intended for personal and
  non-commercial use only"; that "users will not engage in or use any data mining, robots, scraping or similar data
  gathering or extraction methods"; that users may not "use the Service Content in any way for any public or
  commercial purpose"; and that "Any use of the Website and Service Content other than as specifically authorized
  herein is strictly prohibited" (https://support.justwatch.com/article/just-watchs-terms-of-use, reached by
  redirect from justwatch.com/us/terms).

What this means: the code is on the right side of this. It does nothing until a token is set, uses only the
partner endpoints, credits JustWatch and links to its title page. The unofficial GraphQL endpoint that other
trackers use is not an option for a paid product. Two consequences follow. The cost is unknown until JustWatch
quotes, and it may be out of reach for a product earning a few hundred dollars a month. And the links JustWatch
returns carry JustWatch's own attribution (README: "never sent through an affiliate program"), so turning JustWatch
on replaces the app's affiliate income on those rows unless the contract shares revenue.

Without JustWatch, where-to-watch stays a guess from TVmaze's network fields with plain links to each service.
That is lawful (they are ordinary links) but it is not per-country availability, and the terms already say so
(`docs/legal-review.md`, finding 17).

### Smaller points

- Apple's review guideline 5.2.2 applies to all of the above on iOS: "ensure that you are specifically permitted to
  do so under the service's terms of use. Authorization must be provided upon request"
  (https://developer.apple.com/app-store/review/guidelines/).
- Inference: Wikiquote's text is CC BY-SA, but the quoted lines are the shows' own writing. Short quotations with
  credit are common practice; whether they are safe in a paid product in every country is a lawyer's question.
- The service prices shown on network cards were "checked by hand" from a third-party site (README, "Logos and
  prices"). Nothing was verified about that site's terms.

## 2. Affiliate income

| Programme | What pays | Window | Rules that bear on this app |
| --- | --- | --- | --- |
| Amazon Associates (US) | Prime Video Free Trial $2.00; Prime Video First Stream $5.00; Prime Video Stream $0.25; Prime Video add-on subscriptions $2.50 (free trial) and $3.00 (paid); Digital Videos 5.00%; Prime free trial or paid membership $12.00 | A session ends when "24 hours elapse from that click" | Must state "As an Amazon Associate I earn from qualifying purchases"; no cloaking; apps need approval and must "not host or render Amazon web pages in WebViews"; the agreement read covers amazon.com only |
| Apple Services Performance Partners (through Partnerize) | Apple TV: "One-time payment on Membership" (amount not published); TV & Movies: "7% commission on sales revenue" | "30 days" | Links must carry the affiliate token; "we are only accepting a limited number of partners"; "Tracking users at an individual level is not allowed"; minimum payout $30 |
| JustWatch | "Multiple revenue and commission opportunities" | Not published | Only under its partner contract |

Sources: Amazon Associates commission income statement,
https://affiliate-program.amazon.com/help/node/topic/GRXPHT8U84RAYDXZ. Associates Program Policies, updated 14 April
2026, https://affiliate-program.amazon.com/help/operating/policies. Associates Operating Agreement, updated 15
October 2025, https://affiliate-program.amazon.com/help/operating/agreement. Apple,
https://performance-partners.apple.com/commissions-payments, https://performance-partners.apple.com/program-overview
and https://performance-partners.apple.com/home.

Verified rules that affect the design:

- **The `/go/{showId}` redirect and Amazon.** The policies say "You will not cloak, hide, spoof, or otherwise obscure
  the URL of your Site containing Special Links", forbid a "link shortening service, button, hyperlink or other ad
  placement in a manner that makes it unclear" where the link goes, and disqualify links that reach Amazon through
  an intermediate site without a user action. Inference: a clicked, clearly labelled "Watch on Prime Video" button
  that passes through the app's own counter is probably acceptable, but it is the kind of thing Amazon closes
  accounts over. Ask Amazon, or link to Amazon directly and count the click another way.
- **Postbacks.** The app's model is a link template with `{url}` and `{click}` and a server postback per sale
  (README, "Programs are set up in Settings"). That is how affiliate networks work. From memory, unverified: Amazon
  Associates reports by tracking id in its own dashboard and offers no per-click postback, so Amazon sales would not
  appear in the app's report without an import.
- **Subscriptions.** Amazon excludes "any Product purchased as a subscription unless otherwise provided in the
  Agreement"; the bounties in the table are what is provided.
- **Email.** Amazon allows special links in email only to recipients who opted in, so a where-to-watch link in the
  weekly digest needs care.
- **Disclosure.** The US Federal Trade Commission: "The closer the disclosure is to your recommendation, the
  better"; "A single disclosure on your home page won't be sufficient"; "affiliate link" alone is not adequate, while
  wording such as "I get commissions for purchases made through links in this post" is
  (https://www.ftc.gov/business-guidance/resources/ftcs-endorsement-guides-what-people-are-asking). The README says
  the show page now carries "We may earn a commission" on the button and a note beside the links when the flag is
  on, which fits. Amazon's own sentence is a separate requirement and is not in the app.
- **Regions.** Amazon's programme is per country (the agreement read lists the United States and amazon.com).
  Apple pays "based on the product and local market rate". Other countries were not checked.

Not verified: any programme for Netflix (the README says it has none), Disney+, Hulu, Max, Paramount+ or Peacock.
Those run, where they exist, through third-party networks whose terms are behind sign-up; none were read.

What this means: treat affiliate income as a small extra. The verified numbers are $2 to $5 for a new Prime Video
trial or first stream and 5% to 7% on purchases, and they are paid only for new sign-ups or sales inside 24 hours
(Amazon) or 30 days (Apple). Most people who click "Watch on Netflix" already subscribe, which earns nothing. No
conversion rate is published by either programme, so no income figure is given here; the break-even in section 6
assumes none.

## 3. Payments

### Fees

| | Stripe (you are the seller) | Stripe Managed Payments (Stripe is the seller) | Polar (Polar is the seller) |
| --- | --- | --- | --- |
| Per payment | 2.9% + $0.30 (US domestic cards) | The same, plus 3.5% | Starter: 5% + $0.50, no monthly fee. Pro: 3.8% + $0.40 for $20 a month. Growth: 3.6% + $0.35 for $100. Scale: 3.4% + $0.30 for $400 |
| International card | +1.5% | +1.5% | +1.5% |
| Currency conversion | +1% | +1% | Balances are held in USD |
| Subscriptions | Billing: 0.7% of billing volume | Billing as well | None extra on the current plans |
| Tax | Yours to register, collect and file. Stripe Tax: 0.5% per transaction where registered | Included, "in more than 80 countries" | Included: Polar is "liable for registering, collecting, filing and paying sales tax worldwide" |
| Disputes | $15 each | Handled by Stripe | $15 each, "regardless of outcome" |
| Payout | Not read | Not read | 0.25% + $0.25 per payout; cross-border 0% to 1.25%; $2 if no payout in the previous 30 days; 7 days' settlement |

Sources: https://stripe.com/pricing, https://docs.stripe.com/payments/managed-payments,
https://polar.sh/docs/merchant-of-record/fees, https://polar.sh/docs/merchant-of-record/introduction,
https://polar.sh/docs/features/finance/payouts, https://polar.sh/docs/merchant-of-record/supported-countries.

What one Pro subscriber leaves, calculated from the verified rates (tax ignored):

| Payment | Stripe, US card | Stripe, international card | Polar Starter, US card | Polar Starter, international card |
| --- | --- | --- | --- | --- |
| $2.99 monthly | $2.58 (86%) | $2.54 | $2.34 (78%) | $2.30 |
| $29.99 yearly, per month | $2.38 | $2.35 | $2.33 | $2.30 |
| $4.99 Family monthly | $4.51 | $4.44 | $4.24 | $4.17 |

Stripe's column includes the 0.7% Billing fee, since the code makes Checkout Sessions in subscription mode
(`StripePaymentProvider.cs`). The README's "about 22% of $2.99" for Polar is right. Polar's fee is charged "on the
total paid, tax included", so where VAT is added on top the fee rises by a few cents. Polar's $20 plan pays for
itself at about 150 payments a month.

Other verified points:

- **Where the seller can be.** Stripe lists South Africa under "Extended network", linking to Paystack, not as a
  standard country (https://stripe.com/global); the page does not define the term. Polar pays out through Stripe
  Connect Express and lists South Africa as a payout country. This matches the README's reason for adding Polar.
  The owner's country is not known to this research; `docs/legal-review.md` treats South Africa as one possibility.
- **What a merchant of record covers.** Polar: an MoR is "the business that legally sells your product to your
  customers"; it covers VAT, GST and US sales tax, and "You remain responsible for income tax". Its page does not
  say who answers for consumer-law duties or support, so those are not verified either way. Polar's example adds
  tax on top of the listed price for consumers ($10 becomes $12.50 for a Swedish consumer).
- **Refunds.** Polar keeps the original fee on a refund and may itself refund a purchase up to 60 days after it, to
  head off a dispute. A single $15 dispute costs about six months of one Pro subscription.
- **Stripe Managed Payments** supports subscriptions through Checkout, which is how the code already integrates,
  and does not support subscriptions created outside Checkout. Eligibility by seller country was not read.

### Selling to consumers: tax and cancellation rights

**EU.** Verified: a non-EU supplier of electronic services to EU consumers can use the non-Union One Stop Shop,
registering in one member state, with VAT due in each customer's member state, quarterly returns due by the end of
the following month, and "a 'nil return'" when there were no sales. The page sets no threshold for non-EU businesses
(https://vat-one-stop-shop.ec.europa.eu/one-stop-shop/declare-and-pay-oss_en). Verified: consumers can "cancel the
contract within 14 days without providing any justification", for services counted from the day the contract was
agreed; the right is lost for online digital content only where the consumer "expressly agreed to lose your right of
withdrawal", and for services fully performed only "if you expressly agreed to start immediately acknowledging you
would lose the right of withdrawal"
(https://europa.eu/youreurope/citizens/consumers/shopping/returns/index_en.htm).

**UK.** Verified: "If your supplies are liable to UK VAT you will need to register for UK VAT if you are based
outside the UK"; the guidance read states no threshold for overseas sellers; and where a digital platform is the
supplier, "the digital platform is responsible for accounting for VAT on the supply instead of you"
(https://www.gov.uk/guidance/the-vat-rules-if-you-supply-digital-services-to-private-consumers). Unverified at the
primary page: the new subscription-contract regime under the Digital Markets, Competition and Consumers Act 2024
(pre-contract information, renewal reminders, cooling-off periods, easy cancellation). A search of gov.uk and
legislation.gov.uk on 26 November 2026 reported draft regulations with a start date of 1 January 2027 for contracts
made from then, and an earlier official target of spring 2027; the pages themselves were not opened (the one fetched
returned 404). Check https://www.legislation.gov.uk/ukdsi/2026/9780348287912 before relying on a date.

**US.** Verified: the Restore Online Shoppers' Confidence Act makes an online automatic renewal unlawful unless the
seller "clearly and conspicuously discloses all material terms", "obtains a consumer's express informed consent"
before charging, and "provides simple mechanisms for a consumer to stop recurring charges"
(15 U.S.C. § 8403, https://www.law.cornell.edu/uscode/text/15/8403). The FTC's 2024 "Click-to-Cancel" rule is not
in force as written: the FTC's page shows a Federal Register notice of 12 February 2026 revising the Negative Option
Rule "To Conform These Rules to Federal Court Decisions" and a new advance notice of proposed rulemaking in March
2026 (https://www.ftc.gov/legal-library/browse/rules/negative-option-rule). US sales tax on digital subscriptions
varies by state and was not researched; a merchant of record takes it on. State automatic-renewal laws (California's
among them) were not read.

What this means for TvTracker:

- The app already matches the common core of these rules: cancellation from the plan page in one step, the plan kept
  to the end of the paid period, a 14-day refund on a first purchase for everyone, and automatic renewal stated in
  bold (`docs/legal-review.md`, findings 11 and 12). The 14-day refund is offered because the checkout "doesn't
  collect the consent that would waive it". With a merchant of record the checkout is the provider's, so whether
  that consent is collected is the provider's design, not the app's.
- Refunds are made by hand and the app does not act on them (README "Before production"). That is workable at this
  scale and is a known gap.
- **The decision is the Stripe path.** As built it "collects no tax (a decision, for now)". With no threshold for a
  non-EU seller, the first EU or UK consumer sale creates a registration and filing duty. Either sell everywhere
  through Polar or Stripe Managed Payments, or register (OSS for the EU, HMRC for the UK), turn on Stripe Tax and
  file quarterly. For a one-person business the merchant of record is worth its extra 4% to 8%.

## 4. App stores

**Google Play** (Android, built).

- Verified: apps "requiring or accepting payment for access to in-app features or services", subscriptions
  included, must use Play's billing "unless Section 3, 8, or 9 applies", and otherwise "apps may not lead users to a
  payment method other than Google Play's billing system"
  (https://support.google.com/googleplay/android-developer/answer/9858738).
- Verified: "Google Play allows any app to be consumption-only, even if it is part of a paid service", for example
  "a user could log in when the app opens and access content paid for somewhere else"
  (https://support.google.com/googleplay/android-developer/answer/10281818). **This is what the app does today, and
  it carries no fee.**
- Verified, recent changes: from 30 June 2026 in the US, EEA and UK, Google separated a service fee from a billing
  fee. The service fee is 10% on the first $1M of yearly earnings and 10% on all auto-renewing subscriptions,
  "regardless of whether the developer uses Google Play billing, alternative billing, or external web links"; the
  billing fee is 5% and applies only when Play's billing is used (Android Developers Blog, 24 June 2026,
  https://android-developers.googleblog.com/2026/06/play-expanded-billing.html; fee page,
  https://support.google.com/googleplay/android-developer/answer/112622). So a subscription sold in the app costs
  15% through Play's billing, or 10% plus the app's own payment fees through an alternative. In other markets the
  page still shows 15% for subscriptions. The fee page's tables did not extract cleanly; confirm the cells before
  relying on them.
- Verified: the US external content links programme lets an enrolled app link US users to a web checkout. It needs
  enrolment, integration of Google's external links APIs, support and refunds for those purchases, and reporting of
  every transaction made "within 24 hours of following a link", renewals included, with fees from 1 October 2026
  (https://support.google.com/googleplay/android-developer/answer/16470497, as updated 22 July 2026). Google says the
  programme follows a US court order and may change.
- Costs: "US$25 one-time registration fee"; personal accounts must complete a testing requirement before publishing
  (https://support.google.com/googleplay/android-developer/answer/6112435).

**Apple App Store** (iOS, not built).

- Verified, guideline 3.1.1: "If you want to unlock features or functionality within your app, (by way of example:
  subscriptions...), you must use in-app purchase." Guideline 3.1.3(b): multiplatform services "may allow users to
  access content, subscriptions, or features they have acquired in your app on other platforms or your web site...
  provided those items are also available as in-app purchases within the app." Outside the United States
  storefront, apps "may not include buttons, external links, or other calls to action that direct customers to
  purchasing mechanisms other than in-app purchase", and entitlements "are not required for developers to include
  buttons, external links, or other calls to action in their United States storefront apps"
  (https://developer.apple.com/app-store/review/guidelines/).
- Inference: Apple is stricter than Google for this app. A Pro plan bought on the web and honoured in an iOS app
  falls under 3.1.3(b), which asks for the same plan to be offered as an in-app purchase. The consumption-only
  design that works on Play is not clearly allowed on iOS for an app that is not a "reader" app, and a TV tracker's
  insights are not "previously purchased content" in 3.1.3(a)'s list. Expect to add StoreKit for iOS.
- Verified: commission is 15% in the Small Business Program for developers "who made up to 1 million USD in
  proceeds in the prior calendar year" and for new developers
  (https://developer.apple.com/app-store/small-business-program/); the developer programme is "99 USD per
  membership year" (https://developer.apple.com/programs/whats-included/).
- Verified, EU: from 1 October 2026 Apple's unified EU terms set, for Small Business Program members, 15% on Apple
  in-app purchase, 10% on alternative payment processing in the app, and a 10% store services commission on
  out-of-app offers with an actionable link, where "Only sales made within 7 days of the link tap are subject to
  this commission". The developer collects and remits tax on alternative payments and reports them monthly
  (https://developer.apple.com/support/dma-and-apps-in-the-eu/).
- US link-outs, partly unverified: the guideline text above is verified, so a US-storefront app may link to web
  checkout. Whether Apple may charge a commission on those purchases is in litigation. From secondary reporting, not
  a primary source: the Ninth Circuit in December 2025 upheld the contempt finding but allowed a fee to be set on
  remand, and the amount was still before the district court in 2026. No Apple page read today states a US link-out
  fee.

What this means: keep Android consumption-only; it is free and already built. If in-app sales are ever wanted,
budget 15% on both stores, which leaves $2.54 of $2.99 before tax, about the same as Stripe, with the store handling
tax. iOS will probably need in-app purchase from the start.

## 5. Privacy obligations

Verified from the Regulation's text (GDPR, Regulation (EU) 2016/679,
https://eur-lex.europa.eu/legal-content/EN/TXT/HTML/?uri=CELEX:32016R0679):

| Duty | Article | In the code? |
| --- | --- | --- |
| Applies to people "who are in the Union" whatever the operator's country | 3(2) | n/a |
| A lawful basis for each use: consent, contract, legal obligation, vital interests, public task or legitimate interests | 6(1) | Not code. The policy has a table of purposes and bases (`docs/legal-review.md`, finding 1) |
| Tell people who you are, why, the basis, recipients and transfers, when collecting | 13 | The `/privacy` page (`src/Clients/TvTracker.UI/Pages/PrivacyPage.razor`) |
| Answer requests "without undue delay and in any event within one month" | 12(3) | A promise in the policy; export and deletion are self-service |
| Access and a copy | 15 | Yes: `GET api/v1/me/export` across services, tested by `ExportTests` in `tests/Privacy.IntegrationTests` (`CLAUDE.md`) |
| Erasure | 17(1) | Yes: each service handles `UserDeletedIntegrationEvent`, tested in `tests/Privacy.IntegrationTests` |
| A representative in the Union for an operator outside it | 27(1) | Not code. None named (`docs/legal-review.md`, lawyer item 4) |
| A contract with each processor | 28(3) | Not code |
| Records of processing; the exemption for "fewer than 250 persons" has conditions | 30(5) | Not code |
| Security "appropriate to the risk" | 32(1) | Partly: HTTPS only, one front door, encrypted off-site backups (`AppHost.cs`) |
| Breach notice "not later than 72 hours" where feasible | 33(1) | Not code; a promise in the policy |
| Transfers abroad only with a safeguard such as "standard data protection clauses adopted by the Commission" | 44, 46 | Not code |

Articles 20(1) (portability) and 83(5) (fines) were not reached in the text fetched; the export is machine-readable
from the README's description, which is what portability asks for, from memory.

What this means: the engineering half is done and tested, which is unusual at this stage. What remains is paper,
and `docs/legal-review.md` already lists it:

- **Processor agreements** with whoever ends up holding member data: the host, the off-site backup bucket, the
  email sender (Azure Communication Services and the SMTP service), the push service, and Stripe if selling
  directly. With a merchant of record the provider is the seller and holds buyers' payment data on its own account;
  how Polar describes its role under GDPR was not read.
- **Third parties members' browsers reach directly.** TVmaze's images are hot-linked (TVmaze permits it), so
  TVmaze's CDN sees each visitor's address; GIPHY is called from the client by design (`AppHost.cs` comment). Both
  belong in the policy's list of recipients, or the images should be cached and served from the app's own host,
  which TVmaze recommends.
- **EU and UK representatives** if the operator is outside both. These are sold as a yearly service; no price was
  researched, and it is a cost that could exceed hosting.
- **Deletion and tax records.** The app deletes receipts with the account. With a merchant of record the seller's
  records are the provider's, which eases lawyer item 5; the operator still needs its own income records.
- **Play's data safety form and account-deletion address** are described in the README and consistent with the code.

## 6. Running cost versus price

### What it costs

Prices as published on 26 November 2026, excluding VAT. Hetzner's two cloud pages marked every plan "currently
unavailable" when read, so the prices are its list prices, not proof a server can be ordered today.

| Item | Price | Source |
| --- | --- | --- |
| Hetzner CX43: 8 vCPU, 16 GB, 160 GB (cost-optimised, older hardware) | €16.49 a month | https://www.hetzner.com/cloud/cost-optimized/ |
| Hetzner CX53: 16 vCPU, 32 GB, 320 GB | €29.99 | same |
| Hetzner CPX42: 8 vCPU, 16 GB, 320 GB (regular performance) | €59.49 (Helsinki) to €69.99 | https://www.hetzner.com/cloud/regular-performance/ |
| Hetzner CPX62: 16 vCPU, 32 GB, 640 GB | €110.99 to €130.49 | same |
| Hetzner server backups | "Starting at €1.0980" on the €5.49 plan, which is 20% of the server price | same |
| Hetzner Object Storage, for the off-site copy | €6.49 a month, including 1 TB stored and 1 TB egress | https://www.hetzner.com/storage/object-storage/ |
| DigitalOcean Basic droplet: 4 vCPU, 8 GB | $48 | https://www.digitalocean.com/pricing/droplets |
| DigitalOcean Basic droplet: 8 vCPU, 16 GB | $96 | same |
| DigitalOcean General Purpose: 8 vCPU, 32 GB | $252 | same |
| Email through Azure Communication Services | $0.00025 an email plus $0.00012 a MB (Microsoft calls its figures illustrative) | https://learn.microsoft.com/en-us/azure/communication-services/concepts/email-pricing |
| Azure Notification Hubs, Free tier | 1 million pushes, 500 active devices; paid tiers' prices did not load | https://azure.microsoft.com/en-us/pricing/details/notification-hubs/ |
| Apple Developer Program | $99 a year | section 4 |
| Google Play registration | $25 once | section 4 |
| Postgres, Redis, RabbitMQ, Keycloak, flagd, Caddy, Model Runner | Free software, run on the same machine | `AppHost.cs` |

Inference on sizing; nothing was load-tested for this. The stack's memory use is set by its 21 containers, not by
members: the README's 8 GB minimum is the floor, and 16 GB gives room for Keycloak, Postgres and the embedding
model together. A tracker's members are mostly idle, so 1,000 members is a handful of concurrent users and 10,000
is a few dozen; one 16 GB machine should carry the first and one 32 GB machine the second. The design cannot scale
out as it stands (README: "One copy of each service"), so beyond one large machine there is engineering to do
first.

Monthly cost, assuming €1 = $1.15 (an assumption, not a quoted rate):

| | Up to 1,000 members | Up to 10,000 members |
| --- | --- | --- |
| Low: Hetzner cost-optimised | CX43 €16.49 + backups €3.30 + storage €6.49 = €26.28, about **$30** | CX53 €29.99 + €6.00 + €6.49 = €42.48, about **$49** |
| Middle: Hetzner regular | CPX42 €59.49 + €11.90 + €6.49 = €77.88, about **$90** | CPX62 €110.99 + €22.20 + €6.49 = €139.68, about **$161** |
| High: DigitalOcean | 16 GB $96, backups not priced here | 32 GB General Purpose $252 |
| Email (8 per member a month, an assumption) | 8,000 emails, about $2 | 80,000 emails, about $20 |
| Apple programme, if iOS ships | $8.25 | $8.25 |
| Domain | A few dollars; not researched | same |
| **Total, rounded** | **$40 to $110** | **$80 to $285** |

Not in the table, because no price was found or it depends on the owner: a JustWatch contract; a TMDB agreement;
TheTVDB above $50k revenue ($1,000 a year); EU and UK representatives; a lawyer's and an accountant's time; company
registration; push beyond 500 devices; the owner's own time. Any one of the first, fourth or fifth can exceed the
hosting bill.

### What covers it

Using the net figures from section 3: about **$2.30** per monthly Pro subscriber through Polar Starter and **$2.55**
through Stripe (before any VAT the seller must hand over on the Stripe path).

| Monthly cost | Payers needed, Polar | Payers needed, Stripe | Share of 1,000 members | Share of 10,000 members |
| --- | --- | --- | --- | --- |
| $40 | 18 | 16 | 1.6% to 1.8% | |
| $110 | 48 | 44 | 4.4% to 4.8% | |
| $80 | 35 | 32 | | 0.3% to 0.4% |
| $285 | 124 | 112 | | 1.1% to 1.2% |

What this means:

- Hosting is not what makes this uneconomic. At 10,000 members about one member in a hundred paying covers even the
  expensive host. At 1,000 members the cheap host needs about 2% and the expensive one nearly 5%, which is a real
  conversion target for a free tier this generous: tracking, ratings, lists and the basic insights are all free, and
  Pro sells 17 extra insights. From memory, unverified: freemium consumer apps commonly convert low single-digit
  percentages.
- The costs that could sink it are the ones without a price above: a data contract, representatives, professional
  fees. $1,000 a year of any of them is another 36 Polar subscribers.
- Covering cost is not paying the owner. 500 subscribers, which is 5% of 10,000 members, is about $1,150 a month
  after fees and before costs and income tax.
- Yearly plans are worth steering people to on Polar: the fixed $0.50 is paid once, and although the price is ten
  months' worth, the net per month is about the same ($2.33 against $2.34).
- A Family plan with six people on it earns $4.24, or $0.71 a person.

## Open questions for the owner or a lawyer

1. **Where is the operator established?** It decides whether Stripe is available at all, which tax registrations
   apply, whether EU and UK representatives are needed, and the governing law. Everything in section 3 turns on it.
2. **Merchant of record or direct?** If direct through Stripe: who registers for EU OSS and UK VAT and files the
   returns, and is Stripe Tax turned on? As built, the Stripe path collects none.
3. **Share-alike.** Does showing a catalog built from TVmaze's data oblige the operator to license that catalog
   under CC BY-SA, and are derived insights "adapted material"? The app's terms and `LICENSE` should then be worded
   to match. Asking TVmaze directly is cheap and they invite it.
4. **JustWatch.** Ask for a quote and the contract's caching, attribution and revenue-share terms before deciding
   whether per-country availability is worth it. Until then the token stays empty.
5. **TMDB.** Keep the key unset on a paid deployment, or get a written agreement.
6. **Amazon and the `/go` redirect.** Confirm with Amazon that a first-party click counter in front of a special
   link is acceptable, add its required sentence, and get the mobile app approved if links appear there.
7. **iOS.** Is a web-only purchase acceptable to App Review for this app, or is in-app purchase needed from the
   first release? Assume the second when planning.
8. **Consumer-law duties under a merchant of record.** Who gives the pre-contract information, the withdrawal
   notice and the refund when Polar is the seller? Read Polar's seller terms; they were not read here.
9. **UK subscription rules.** Confirm the commencement date and whether the app's renewal reminders and cancellation
   flow meet them. No renewal reminder is sent today as far as the README describes.
10. **GDPR paper.** Processor agreements, a record of processing, representatives, and a transfer safeguard, as
    `docs/legal-review.md` lists.
11. **Wikiquote lines** in a paid product: a copyright question about the quotations themselves, not the licence.

## Could not verify

- JustWatch Partner API pricing, eligibility and contract terms: not published.
- Apple TV membership bounty amount: the page says only "One-time payment on Membership".
- Apple's fee, if any, on US link-out purchases: secondary reporting only.
- UK DMCC subscription regime commencement: from a search summary of official pages, not the pages themselves.
- Amazon's rule on closing accounts without sales in the first 180 days: looked for, not found in the pages read.
  The mobile-app approval process and programmes outside the US were not read.
- Affiliate programmes for Netflix, Disney+, Hulu, Max, Paramount+ and Peacock: not researched to a primary source.
- Google Play's fee tables for non-subscription purchases: the page extracted ambiguously.
- TheTVDB's "user-supported" key with a subscriber PIN: in the README, not on the pages read today.
- TVmaze Premium prices (placeholders on the page) and any commercial API price.
- Azure Notification Hubs paid tier prices (blank on the page); Azure email prices are Microsoft's "illustrative" ones.
- Hetzner availability: all plans showed as unavailable when read.
- GDPR articles 20(1) and 83(5): not reached in the fetched text.
- Stripe's "Extended network" for South Africa: undefined on the page; Managed Payments eligibility by country not read.
- Polar's own role and terms on consumer law, support and data protection.
- US state sales tax and state automatic-renewal laws: not researched.
- The EUR to USD rate used (1.15) is an assumption.
- Typical freemium conversion rates: from memory.

## Sources

All read 26 November 2026.

Data sources
- TVmaze API and licensing: https://www.tvmaze.com/api
- TVmaze Premium: https://www.tvmaze.com/premium
- CC BY-SA 4.0 legal code: https://creativecommons.org/licenses/by-sa/4.0/legalcode.en
- TheTVDB API information: https://thetvdb.com/api-information
- TheTVDB API signup and pricing tiers: https://thetvdb.com/api-information/signup
- Wikidata data access: https://www.wikidata.org/wiki/Wikidata:Data_access
- Wikimedia Foundation Terms of Use: https://foundation.wikimedia.org/wiki/Policy:Terms_of_Use
- TMDB API Terms of Use: https://www.themoviedb.org/api-terms-of-use
- TMDB API for Business: https://www.themoviedb.org/api-for-business
- JustWatch Partner API documentation: https://apis.justwatch.com/docs/api/
- JustWatch Streaming API (business page): https://www.justwatch.com/us/JustWatch-Streaming-API
- JustWatch Terms of Use (updated 5 May 2026): https://support.justwatch.com/article/just-watchs-terms-of-use
- GIPHY API documentation: https://developers.giphy.com/docs/api/

Affiliates
- Amazon Associates Program Policies (updated 14 April 2026): https://affiliate-program.amazon.com/help/operating/policies
- Amazon Associates Operating Agreement (updated 15 October 2025): https://affiliate-program.amazon.com/help/operating/agreement
- Amazon Associates commission income statement: https://affiliate-program.amazon.com/help/node/topic/GRXPHT8U84RAYDXZ
- Apple Services Performance Partners: https://performance-partners.apple.com/home, https://performance-partners.apple.com/commissions-payments, https://performance-partners.apple.com/program-overview
- FTC, Endorsement Guides: What People Are Asking: https://www.ftc.gov/business-guidance/resources/ftcs-endorsement-guides-what-people-are-asking

Payments, tax and consumer law
- Stripe pricing: https://stripe.com/pricing
- Stripe Managed Payments: https://docs.stripe.com/payments/managed-payments
- Stripe global availability: https://stripe.com/global
- Polar fees: https://polar.sh/docs/merchant-of-record/fees
- Polar merchant of record: https://polar.sh/docs/merchant-of-record/introduction
- Polar payouts: https://polar.sh/docs/features/finance/payouts
- Polar supported countries: https://polar.sh/docs/merchant-of-record/supported-countries
- European Commission, One Stop Shop: https://vat-one-stop-shop.ec.europa.eu/one-stop-shop/declare-and-pay-oss_en
- Your Europe, returns and withdrawal: https://europa.eu/youreurope/citizens/consumers/shopping/returns/index_en.htm
- HMRC, VAT on digital services to consumers: https://www.gov.uk/guidance/the-vat-rules-if-you-supply-digital-services-to-private-consumers
- Draft Consumer Subscription Contracts Regulations 2026 (found by search, not opened): https://www.legislation.gov.uk/ukdsi/2026/9780348287912
- 15 U.S.C. § 8403 (text as hosted by Cornell's Legal Information Institute): https://www.law.cornell.edu/uscode/text/15/8403
- FTC, Negative Option Rule: https://www.ftc.gov/legal-library/browse/rules/negative-option-rule

App stores
- Google Play Payments policy: https://support.google.com/googleplay/android-developer/answer/9858738
- Understanding Google Play's Payments policy (FAQ): https://support.google.com/googleplay/android-developer/answer/10281818
- Google Play service fees: https://support.google.com/googleplay/android-developer/answer/112622
- Google Play, US external content links programme: https://support.google.com/googleplay/android-developer/answer/16470497
- Android Developers Blog, 24 June 2026: https://android-developers.googleblog.com/2026/06/play-expanded-billing.html
- Google Play developer registration: https://support.google.com/googleplay/android-developer/answer/6112435
- Apple App Review Guidelines: https://developer.apple.com/app-store/review/guidelines/
- Apple App Store Small Business Program: https://developer.apple.com/app-store/small-business-program/
- Apple, apps in the EU: https://developer.apple.com/support/dma-and-apps-in-the-eu/
- Apple Developer Program: https://developer.apple.com/programs/whats-included/
- Secondary, for the US link-out fee litigation only: https://www.macrumors.com/2025/12/11/apple-app-store-fees-external-payment-links/

Privacy
- GDPR, Regulation (EU) 2016/679: https://eur-lex.europa.eu/legal-content/EN/TXT/HTML/?uri=CELEX:32016R0679

Hosting
- Hetzner Cloud: https://www.hetzner.com/cloud/cost-optimized/, https://www.hetzner.com/cloud/regular-performance/
- Hetzner Object Storage: https://www.hetzner.com/storage/object-storage/
- DigitalOcean droplets: https://www.digitalocean.com/pricing/droplets
- Azure Communication Services email pricing: https://learn.microsoft.com/en-us/azure/communication-services/concepts/email-pricing
- Azure Notification Hubs pricing: https://azure.microsoft.com/en-us/pricing/details/notification-hubs/

Repository
- `README.md`, `docs/legal-review.md`, `CLAUDE.md`
- `src/TvTracker.IntegrationEvents/Plans.cs`
- `src/Services/Financials/Financials.API/Payments/` and `Services/FinancialStatement.cs`
- `src/Services/Catalog/Catalog.SyncWorker/` (`Providers/`, `JustWatch/`, `Quotes/`, `Supplement/`)
- `src/TvTracker.AppHost/AppHost.cs`
