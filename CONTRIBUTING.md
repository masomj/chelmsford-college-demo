# Contributing

How the code is organised, the conventions we follow, and how to add common things. Read [PLAN.md](PLAN.md) first for what the demo must do and why.

**Keep this file up to date.** If you add or change a shared component, a design token, a naming convention or one of the "how to add" recipes below, update this file in the same change.

## Project layout

```
Chelmsford.slnx
Directory.Build.props        net10.0, nullable, implicit usings for every project
Directory.Packages.props     every package version (central package management)
src/
  Chelmsford.Core/           domain models, interfaces and pure logic. No Umbraco, EF Core or HTTP.
    Access/                  access log contract (IAccessLog, AccessLogEntry, IpAnonymiser)
    Common/                  text helpers for the college's raw values (SourceText) and UkTime (UK civil time)
    Courses/                 SyncedCourse, search, vocabulary (levels, funding), demo campus mapping
    Events/                  event booking model
    Integration/             source row models, the source reader interface, sync contracts
    Messaging/               outbox email model
    Payments/                payable items, orders, links, webhook events, payment service contracts
  Chelmsford.Infrastructure/ implementations of the Core interfaces
    Data/                    SiteDbContext (EF Core, SQLite in the demo)
                             plus the source reader, sync service, payment services and gateways
  Chelmsford.Web/            the Umbraco 17 site: composers, document types, seeding,
                             controllers, view models, Razor views, CSS and JS
tests/
  Chelmsford.Tests/          xUnit tests for Core and Infrastructure
tools/                       Python scripts that build college-content/ from the college's files
deploy/                      Caddy block and deployment notes
college-content/             gitignored: the college's images and spreadsheet export
```

Dependencies only point inwards: Web uses Infrastructure and Core, Infrastructure uses Core, and Core uses nothing of ours. Put logic in Core when it can be tested without a database or Umbraco.

## Where things live

### Document types are defined in code

Content types, data types and their properties are created in code at startup, so the site builds itself from an empty database (in the container and on a new laptop). Do not create document types by hand in the back office: they would not exist on the next fresh install. Change the code instead and restart.

Sample content (homepage, campaigns, events, payable items, media from `college-content/images`) is seeded on the first run only. Seeding must be safe to run again: check whether something exists before creating it.

### One controller per document type

Routing uses Umbraco route hijacking. Each document type that needs logic has a controller named `{DocTypeAlias}Controller` (for example `CourseController` for `course`) that derives from `SitePageController`. The controller builds a view model and returns `Page("Course", model)`, which renders `~/Views/Pages/Course.cshtml`, so a view never queries services itself and no Umbraco templates are needed. `SitePageController` also seals the synchronous `Index` and asks you to write an `async Task<IActionResult> Index(...)` instead, and gives you `NotFoundPage()` for a 404 with the friendly page.

`/courses` is the `courseListing` content node, hijacked by `CourseListingController`, which runs the search (`SearchWithSynonymsAsync`, so "chef" finds the cookery course) and renders the listing from the query string. `HomeController` and `CampaignController` both build the homepage through `HomePageBuilder`, so a campaign preview is exactly the homepage with that campaign in the hero (and is marked `noindex`). Document types without logic use the default controller and a view named after the alias.

Non-content routes (`/make-a-payment` and `/pay/*`, `/how-the-data-gets-here`, `/reports/access`, `/pay/webhook/stripe`, `.ics` downloads, `/healthz`) are ordinary ASP.NET Core controllers or endpoints with explicit routes. Their views live in `Views/Pay`, `Views/Demo` and `Views/Reports`, each with a `_ViewStart.cshtml` that sets the shared layout.

### Request pipeline (`Program.cs`)

In order: forwarded headers, the rate limiter, security headers and `/healthz`, college image files, page-view tracking, then Umbraco. `/healthz` is `200 ok` only when Umbraco is at `RuntimeLevel.Run` and `ISiteReadiness.IsReady`, otherwise `503` with the level as text. Rate limits are defined in `Services/RateLimitRules.cs`; to limit another POST endpoint, add a rule there. Page views are queued by `PageViewTracking` and written by `PageViewRecorder`; add a prefix to its skip list if a new route should not appear in the access report.

### Services in the Web project

| Service | What it is for |
|---|---|
| `SiteContent` | Reads published CMS content (campaigns, events, course URLs) for controllers |
| `HomePageBuilder` | Builds the homepage view model from the Home node and an optional campaign |
| `CourseSearchSynonyms` | Search synonyms ("chef" to cookery, "plumber" and so on) |
| `RichText` / `Html.RichText` | Sanitises CMS and synced HTML. Use it instead of `Html.Raw` for anything an editor or the source data can write |
| `TextHelpers` | `SafeUrl`, JSON-LD, video embed links, duration and price parsing |
| `UkTime` | Display formatting; the time zone and conversions come from `Chelmsford.Core.Common.UkTime` |
| `PayableItemRules` | When a payable item counts as closed |
| `ReportsAuthenticator` | Basic sign-in and lockout for the access reports |

### Integration layer

- `ICollegeSourceReader` reads the college's tables through a read-only connection and returns them as they are stored (`SourceSnapshot`).
- `ICourseSyncService` cleans the snapshot into `SyncedCourse` records and brings the site database up to date. It records a `SyncRun` each time.
- `ICourseSyncObserver` implementations are told after each successful sync (for example, to create a CMS node for each new course).
- Background sync waits for `ISiteReadiness` so it doesn't run before the CMS schema and seed content exist.

Visitors are always served from the site database, never from the source.

### Payments flow

1. Staff create payable items in the CMS (`IPayableItemCatalogue` reads them).
2. The payer reaches their due items by one of three routes: demo student sign-in, a single-use payment link (`IPaymentLinkService`; only a hash of the token is stored) or the guest lookup by student ID and date of birth (rate limited).
3. `IPaymentService.StartOrderAsync` creates a `PaymentOrder` before checkout. A unique index on item, subject and an active marker stops a second order for something already paid or in progress. Trip places are reserved in the same transaction.
4. `BeginCheckoutAsync` hands the order to the `IPaymentGateway` (Stripe, or the demo gateway when no keys are set) and redirects to its hosted page.
5. The payment is recorded by the webhook (`IPaymentWebhookHandler`, deduplicated by event ID) or on return (`ConfirmReturnAsync`), whichever comes first. `MarkPaidAsync` only has an effect the first time.
6. The confirmation page is only shown once the order is `Paid`.

To support another provider (for example WPM Education or Pay360), add an `IPaymentGateway` implementation. Nothing else should need to change.

### Configuration

Settings are read from `appsettings.json`, then `appsettings.{Environment}.json`, then `appsettings.Local.json` (Debug builds only, gitignored), then environment variables. In the container every setting comes from environment variables (`Section__Key`), set in the `Dockerfile` and in `.env` on the server. Never commit secrets: use `appsettings.Local.json` locally and `.env` on the server.

| Setting | Purpose |
|---|---|
| `ConnectionStrings:umbracoDbDSN` | Umbraco's database |
| `ConnectionStrings:SiteData` | The site database (synced courses, orders, bookings, outbox) |
| `ConnectionStrings:CollegeSource` | The stand-in college source database, opened read-only |
| `College:ContentPath` | The `college-content` folder (images and source data) |
| `College:DataPath` | Where the site writes its data files |
| `Umbraco:CMS:Global:UmbracoMediaPhysicalRootPath` | Where Umbraco stores media |
| `Payments:Stripe:*` | Stripe test keys; empty means the built-in test checkout |
| `Umbraco:CMS:Imaging:HMACSecretKey` | Signs image resize URLs. Never in `appsettings.json`: `appsettings.Local.json` locally, `Umbraco__CMS__Imaging__HMACSecretKey` in the container |
| `Panel:CmsUser:*` | Name, email and password of the editor login created for the tender panel. Empty means none is created |
| `Reports:UserName`, `Reports:Password` | Sign-in for `/reports/access`. With no password the reports return 404 |
| `Access:NotifyEmail`, `Access:RetentionDays` | Who is emailed about first visits and CMS sign-ins (empty means nobody), and how long the access log is kept (90 days) |
| `Smtp:*` | Outbox sender. With no `Smtp:Host` nothing is sent and emails stay queued |

## CSS and front-end conventions

- **Mobile first.** Base styles are for small screens. Larger layouts are added with `min-width` media queries only.
- **One stylesheet, `wwwroot/css/site.css`.** The `:root` block at the top holds every token. No colour, shadow or radius appears as a raw value anywhere else, and inline `style=""` attributes are avoided (use a class, or `currentColor` for icons). Before adding a token, check whether an existing one fits. Text and background pairs must meet WCAG 2.2 AA contrast (4.5:1 for body text, 3:1 for large text and UI boundaries).
- **Colour rules.** Brand blue `--blue` (#007AC5) for text and buttons on white, navy `--navy` (#0B2540) for headings and dark backgrounds, brand green `--green` (#7BC242) only as a background with navy text, never as text.
- **Design tokens.**

  | Group | Tokens |
  |---|---|
  | Brand | `--blue`, `--blue-hover`, `--blue-dark`, `--navy`, `--green`, `--green-hover`, `--green-dark` |
  | Text | `--ink`, `--muted`, `--slate`, `--white`, `--black`, `--on-navy`, `--error` |
  | Surfaces and tints | `--tint-blue`, `--tint-green`, `--tint-grey`, `--tint-selected`, `--tint-hover`, `--warning-bg`, `--code-bg`, `--code-text` |
  | Lines | `--line`, `--line-soft`, `--line-strong`, `--info-line`, `--warning-line`, `--line-on-navy` |
  | Shape and depth | `--radius-sm`, `--radius`, `--radius-lg`, `--radius-xl`, `--shadow`, `--shadow-lg`, `--shadow-up`, `--shadow-card` |
  | Layout and focus | `--font`, `--gutter`, `--wrap`, `--target` (44px touch target), `--focus` |

- **Font.** Figtree is self-hosted from `wwwroot/fonts/figtree-latin.woff2` (one variable latin file, `font-display: swap`, preloaded in the layout). Do not add a Google Fonts link.
- **Naming.** Class names follow BEM: `block`, `block__element`, `block--modifier` (for example `result-card`, `acc__trigger`, `btn--ghost`). State uses ARIA attributes (`[aria-expanded="true"]`, `[aria-current]`) or `is-` classes. JavaScript hooks use `data-` attributes (`data-acc-trigger`, `data-autofocus`, `data-cookie-reopen`), not styling classes. Small utility classes (`.mt-m`, `.mb-m`, `.page-top`, `.page-bottom`) cover one-off spacing.
- **Accessibility basics.** Every page keeps the skip link and visible focus styles. Touch targets are at least 44 by 44 px. Animations respect `prefers-reduced-motion`. Every form field has a real `<label>`, with hints under the label and linked by `aria-describedby` (never placeholders), and errors in an error summary. Nothing submits on change; there are no `alert` or `confirm` dialogs. `scroll-padding` keeps focused and jumped-to elements clear of the sticky bars. Anything that scrolls sideways (wide tables, `<pre>`) gets `tabindex="0"`, `role="region"` and an `aria-label`; `RichText` does this for tables automatically.
- **Disclosure sections.** Course sections are `div[data-acc-section] > h2 > button[aria-expanded][aria-controls]` plus a `div[data-acc-panel][role=region]`. Everything is open in the HTML, so it works without JavaScript; `site.js` then closes all but the first. Jump links open their target section.
- **Cookie notice.** A normal block straight after the skip link (not a fixed overlay), shown by `site.js` until a choice is made, and reopened from the "Cookie settings" link in the footer (`data-cookie-reopen`).
- **No build step.** CSS and JS are plain files in `wwwroot`, so the container build needs only the .NET SDK.

### Shared view pieces

| Piece | Where | Use it for |
|---|---|---|
| `_Layout` | `Views/Shared/_Layout.cshtml` | Page shell: skip link, cookie notice, header and navigation (`aria-current="true"` for a section, `"page"` for the exact page via `Html.AriaCurrent`), footer, JSON-LD, `noindex` when the model says so |
| `_Breadcrumb` | `Views/Shared/_Breadcrumb.cshtml` | Breadcrumb trail from a list of (text, url); the last item, with a null url, is the current page |
| `_DateBadge` | `Views/Shared/_DateBadge.cshtml` | Month and day badge for an event |
| `_ErrorSummary` | `Views/Shared/_ErrorSummary.cshtml` | "There is a problem" list linking to each invalid field; keyed by field id |
| `_PayHeader` | `Views/Shared/_PayHeader.cshtml` | Back link, "Step N of 3" progress bar and error summary for every payment step |
| `_Video` | `Views/Shared/_Video.cshtml` | Click-to-load video (nothing is fetched until play), with a poster image |
| `Html.Picture`, `Html.RichText`, `Html.AriaCurrent` | `Services/HtmlExtensions.cs` | Responsive images, sanitised HTML, `aria-current` |

When you add a shared component (a partial or view component used by more than one page), change a token, or add a utility class, update the lists above in the same change.

## Naming in C#

- One public type per file, file named after the type.
- Interfaces start with `I`. Async methods end in `Async` and take a `CancellationToken`.
- Money is held as whole pence (`int AmountPence`), never `decimal` pounds.
- Times are stored in UTC (`...Utc` properties) and converted to UK time only for display, with `Chelmsford.Core.Common.UkTime`. Dates an editor types into the CMS have no zone and are read as UK time (`UkTime.AsLocal`).
- Column and property names from the college's schema (`C_TITLE`, `CL_COURSEID`) are kept as they are in source models, so they can be matched to the college's tables.

## How to add things

### A document type

1. Add the alias to `DocTypes` and any new property aliases to `DocTypes.Props` (`Umbraco/DocTypes.cs`). Use camelCase (`eventListing`, `payableItem`).
2. Define the document type, its properties, allowed children and any data type in `Umbraco/SchemaInstaller.cs`, next to the existing ones. The installer is idempotent: it creates what is missing and never deletes. To rename a property label, give the property a `FormerNames` entry so an existing install is renamed rather than duplicated.
3. If the page needs logic, add `{Alias}Controller` deriving from `SitePageController` (not `RenderController`), with an `async Task<IActionResult> Index(...)` that builds a view model (add it to `Models/PageModels.cs`, deriving from `PageViewModel`) and returns `Page("{View}", model)`.
4. Add the view as `Views/Pages/{View}.cshtml`. It takes the view model and does no querying.
5. If the site should have one by default, add it to `ContentSeeder.SeedBaseContent` (first run only). If existing installs also need it, add an idempotent step that checks first, and call it from `StartupHandler`.
6. Restart the site. The schema is applied at startup. Test from an empty database as well as an existing one.

### A page

For content pages, create them in the back office under the right parent; the document type decides the view. For pages that must exist on every fresh install, add them to the seeding code (`ContentSeeder`). For a page that isn't content (a form handler, a report or a download), add an ASP.NET Core controller with an explicit route, a view folder with its own `_ViewStart.cshtml` that sets the shared layout, and a view model deriving from `PageViewModel`. If the page is private or should not be indexed, set `NoIndex`, and add its prefix to the skip list in `PageViewTracking` if it should not appear in the access report.

### A payable item type

1. Add the value to `PayableItemType` in `Chelmsford.Core/Payments/PaymentModels.cs`. Append it at the end: values are stored as numbers, so don't renumber existing ones.
2. Add its label in `PayableItem.TypeLabel` and, if it is not paid by enrolled students, update `PayableItem.AppliesTo`.
3. Add it to the "Payable item type" data type and to the type handling in `UmbracoPayableItemCatalogue` in the Web project, so the CMS offers it and the catalogue reads it.
4. If it needs special rules (places, deadlines, who can pay), put them in the Core payment logic (`PayableItemRules` in Web decides when an item shows as "Payment closed") and add tests in `tests/Chelmsford.Tests`.
5. Check that the Stripe metadata, the "At a glance" additional costs on course pages and the confirmation page show it correctly.

## Before you push

```powershell
dotnet build -c Release
dotnet test -c Release
```

Pushes to `main` are tested and then deployed to the dev server automatically (see [deploy/DEPLOY.md](deploy/DEPLOY.md)), so `main` must always build and pass its tests. Use a pull request for anything risky: pull requests run the same build and tests without deploying.

Writing style for user-facing text and docs: plain UK English, short sentences, no em dashes.
