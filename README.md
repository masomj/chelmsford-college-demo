# Chelmsford College website demo

A working demo website built by [Vanguard Digital Solutions](https://vanguarddigitalsolutions.co.uk) for Chelmsford College's website tender. It follows the architecture described in our bid, so the evaluation panel can see and use the real approach rather than a mock-up.

- Live demo: https://chelmsford.vanguarddigitalsolutions.co.uk
- CMS back office: https://chelmsford.vanguarddigitalsolutions.co.uk/umbraco (accounts are given to the panel separately)

This is a demo. It is not the college's website and is not endorsed by the college. Payments run in test mode only and no real money can move.

## What it shows

| Demo brief requirement | In the demo |
|---|---|
| Homepage with logo and main navigation | Shared layout and homepage |
| Prominent course search, with results filtered by subject, level, study mode and campus | Search box on the homepage, results at `/courses` with filters |
| Prominent area for key messages and campaigns | Campaigns in the CMS with start and end dates; the homepage shows the one that is live |
| Areas for video and images | Click-to-load video block and photo cards, managed in the CMS |
| Course pages from the college's database, one template for every course | The integration layer syncs the college's course tables into the site, and one `Course` template renders them all |
| Course page that is easy to scan on mobile | Header and image, "At a glance", jump links, expandable sections and a sticky "Apply now" |
| Events listing with filters | `/events`, filtered by type and month |
| Single event page with date, time, location, description and booking | Event template, booking form and add-to-calendar (.ics) |
| Payments for deposits, materials fees, trips and one-off costs, by students or parents and carers | `/make-a-payment`: demo student sign-in, secure single-use payment links and a guest lookup; Stripe hosted checkout |
| Mobile first, WCAG 2.2 AA | Mobile-first CSS, skip link, visible focus, reduced motion support, real labels and hints, large touch targets |
| Show the CMS | Umbraco 17 back office at `/umbraco` |

It also includes a "How the data gets here" page that shows the college's raw data next to the cleaned version and when the last sync ran.

## Architecture

- **Platform:** Umbraco 17 LTS on ASP.NET Core and .NET 10. Pages are rendered on the server.
- **Projects:**
  - `src/Chelmsford.Core`: domain models, interfaces and pure logic (text cleaning, level and funding mapping, payment rules).
  - `src/Chelmsford.Infrastructure`: EF Core site database, the college source reader, the sync service, and the Stripe and demo payment gateways.
  - `src/Chelmsford.Web`: the Umbraco site: document types defined in code, content seeding, controllers and Razor views.
  - `tests/Chelmsford.Tests`: xUnit tests for the cleaner, mapping, payment idempotency and guest lookup limits.
- **College source database:** a stand-in for the college's SQL Server. It is a SQLite file built from the supplied spreadsheet, keeping its quirks and its table names (`courses`, `course_sections`, `course_levels`, `course_level_link`, `course_funding`, `course_fund_link`), plus demo `students` and `applications` tables for the payment journeys. The integration layer opens it read-only and never writes to it.
- **Integration layer:** reads the source, cleans the data (raw HTML with Bootstrap 3 glyphicons, the text `'NULL'` instead of real nulls, `_x000D_` artefacts, fees held as free text, levels and funding only reachable through link tables) and keeps the site's own copy in sync every few minutes. Visitors are served from that copy, so the site keeps working if the source is unavailable.
- **Site database:** EF Core tables for synced courses, sync runs, payable item state, orders, payment links, webhook events, event bookings and the email outbox. In production these would sit in the website's SQL Server database. In the demo they are a separate SQLite file next to Umbraco's, so two ORMs don't lock one SQLite file.
- **CMS:** every synced course gets a CMS node where editors can add an image, a video and a student story. The synced facts are read-only in the CMS.
- **Payments:** staff create payable items in the CMS. An order is created with a unique key before checkout, an item that is already paid can't be paid again, webhook events are deduplicated by event ID, trip places are reserved inside a transaction, and confirmation is shown only after the payment is recorded. Card details go straight to Stripe's hosted checkout and never touch our server.
- **Structured data:** JSON-LD for `Course` and `Event`.

[PLAN.md](PLAN.md) has the full plan, and [CONTRIBUTING.md](CONTRIBUTING.md) explains where things live in the code.

## College content is not in git

This repository is public. The college's logos, photos and course spreadsheet came to us in a tender pack, so they are not committed. They live in a gitignored `college-content/` folder:

```
college-content/
  images/        logo-colour.png, logo-white.png, and each photo at 480, 960 and 1600 px wide (JPEG and WebP)
  source-data/   one JSON file per spreadsheet sheet, values exactly as the college stores them
```

The two scripts in `tools/` build this folder from the files the college supplied. In the container the folder is mounted read-only at `/app/college-content`. Without it the site still runs, with a text wordmark in place of the logo, but there are no courses or photos.

## Running locally

### Prerequisites

- .NET SDK 10 (see `global.json`)
- Python 3 with `openpyxl` and `Pillow` (`pip install openpyxl Pillow`)
- The college's files: `Appendix 2 - Demo Course Data.xlsx` and `Re__Demo_images_.zip` (both are read from your Downloads folder by default)

### Steps

From the repository root:

```powershell
python tools/export-demo-data.py      # or: python tools/export-demo-data.py "path\to\Appendix 2 - Demo Course Data.xlsx"
python tools/prepare-images.py        # or: python tools/prepare-images.py "path\to\Re__Demo_images_.zip"
```

Create `src/Chelmsford.Web/appsettings.Local.json` (gitignored, and only loaded in Debug builds) so Umbraco installs itself and creates your admin account on the first run. The same file holds your image-signing key, because secrets never go in `appsettings.json`:

```json
{
  "Panel": {
    "CmsUser": { "Name": "Tender panel", "Email": "panel@example.com", "Password": "at-least-ten-characters" }
  },
  "Reports": { "UserName": "you", "Password": "a-long-passphrase" },
  "Umbraco": {
    "CMS": {
      "Unattended": {
        "InstallUnattended": true,
        "UnattendedUserName": "Your Name",
        "UnattendedUserEmail": "you@example.com",
        "UnattendedUserPassword": "at-least-ten-characters"
      },
      "Imaging": { "HMACSecretKey": "a base64 string of 64 random bytes" }
    }
  }
}
```

`Panel` and `Reports` are optional (see "Who has looked at the demo" below). To make an `HMACSecretKey`: `[Convert]::ToBase64String((1..64 | ForEach-Object { Get-Random -Maximum 256 }))` is fine locally; in the container set `Umbraco__CMS__Imaging__HMACSecretKey` to a key from a proper random source.

Then run the site:

```powershell
dotnet run --project src/Chelmsford.Web
```

Open the URL shown in the console. The back office is at `/umbraco`. Run the tests with `dotnet test`.

To start again from an empty database, stop the site and delete the local data folder (the SQLite files under `src/Chelmsford.Web/umbraco/Data/`, and the site and source databases).

### Running the container locally

```powershell
Copy-Item .env.example .env       # then edit .env
docker compose up -d --build
```

The compose file joins the external Docker network that Caddy uses on the dev server. To run it on its own machine, create that network first (`docker network create intercopy_default`) and add a port mapping such as `127.0.0.1:8090:8080`. Deployment to the dev server is described in [deploy/DEPLOY.md](deploy/DEPLOY.md). Every push to `main` is tested and deployed automatically.

## Who has looked at the demo

The demo records who uses it, so we can see how the panel looked around. This is also said on the site's privacy page.

- **Panel CMS login.** If `Panel:CmsUser` has an `Email` and `Password`, start-up creates (or repairs the password of) a CMS user in the Editors group, so the panel never needs the administrator account.
- **What is recorded.** Page views (`GET` requests for HTML pages that return 200; not the back office, `/healthz`, reports or static files), CMS sign-ins, failed sign-ins and sign-outs. Each entry has the time, the user, the page path, a shortened IP address (`81.2.69.x`, never the full address) and the browser's user-agent text cut to 200 characters. Payment addresses are shortened too, so an order reference or payment-link token never reaches the log (`/pay/link/...`).
- **Who the user is.** Page views use the `X-Remote-User` header set by the reverse proxy when it has signed someone in, and "local" otherwise. CMS events use the CMS user's name.
- **How it is recorded.** Page views go on a bounded in-memory queue and a background service writes them, so a slow or failing log never slows a page. If the queue is full, views are dropped.
- **Retention.** Entries are deleted after `Access:RetentionDays` days (default 90). Clean-up runs at most once an hour.
- **Notification emails.** If `Access:NotifyEmail` is set, an email is queued for each user's first page view of the UK day and for every CMS sign-in. They are sent only if SMTP is configured (see "Emails are queued" below).
- **The report.** `/reports/access` shows page views per person per day for 30 days, recent CMS sign-ins and the latest 200 page views, in UK time. `/reports/access.csv` downloads the latest 500 entries. Both use HTTP Basic sign-in checked by the app (`Reports:UserName`, `Reports:Password`), so they stay protected even if the proxy is bypassed. With no `Reports:Password` they return 404. Five wrong attempts from one address lock it out for 15 minutes. The pages are marked `noindex`.

## Health check

`/healthz` answers before Umbraco and without signing in. It returns `200 ok` only when Umbraco is fully running (`RuntimeLevel.Run`) and the CMS schema, seed content and first sync are finished. Otherwise it returns `503` with the runtime level as text, so Caddy and the container don't send traffic to a site that is still starting.

## Safeguards worth knowing about

- **Rate limits** (per client address, per minute): "Sync now" 3, demo payment link 10, event booking 10, guest student lookup 20. A client over the limit gets a friendly 429 page. The site runs behind Caddy, so `UseForwardedHeaders` supplies the real client address.
- **Rich text is sanitised.** CMS and synced course HTML goes through `RichText` (HtmlSanitizer) before it reaches a page, so an editor can't add scripts, and tables become keyboard-scrollable regions.
- **Redirect targets are checked.** `TextHelpers.SafeUrl` accepts only local paths, rejecting backslashes, control characters and `//` prefixes.
- **Fonts are self-hosted.** Figtree (SIL Open Font Licence, licence in `wwwroot/fonts`) is served from the site, so no visitor data goes to Google Fonts.
- **Concurrent CMS logins are allowed**, so the panel and the team can be signed in at the same time.

## Payments test mode

- **No Stripe keys configured (default):** the site uses a built-in test checkout, clearly labelled as a test. The rest of the flow (orders, exactly-once handling, confirmation, emails to the outbox) is the same as with Stripe.
- **Stripe test keys (`sk_test_...`):** set `Payments:Stripe:SecretKey`, `Payments:Stripe:PublishableKey` and `Payments:Stripe:WebhookSecret` to use Stripe's hosted checkout in test mode. Use card `4242 4242 4242 4242` with any future expiry and any CVC. The webhook endpoint is `/pay/webhook/stripe` and listens for `checkout.session.completed`.

Never configure live keys on the demo.

## Known limitations

- **ImageSharp advisories.** Umbraco 17 depends on SixLabors.ImageSharp 3.1.12, which has open security advisories. The fixes are only in ImageSharp 4, which Umbraco 17 does not support, so it can't be upgraded on its own. Umbraco uses it to resize media images. We will move to the fixed version when an Umbraco release supports it.
- **Campus is illustrative.** The demo data has no campus field, and every course is full-time. Campus comes from a labelled demo mapping (subject area to campus) until the real source is confirmed during discovery.
- **Sample content is illustrative.** Events, campaigns, payable items, amounts, students and applicants are invented for the demo.
- **Demo sign-in.** Student sign-in is a labelled demo sign-in. In production it would use the college's Entra ID.
- **Emails are queued, and only sent if SMTP is set up.** Payment receipts, booking confirmations, duplicate-payment alerts to finance and the access notifications below are written to an outbox table in the same save as the thing they describe. A background sender (`OutboxDispatcher`) delivers them only when `Smtp:Host` is set; with no SMTP host nothing is sent and they stay queued. Failed sends are retried with a growing delay (30 seconds, doubling up to an hour). The demo runs with no SMTP host, so the pages say "no email is actually sent". All demo addresses use `example.ac.uk`.
- **SQLite.** The demo uses SQLite files on a mounted volume. Production would use the college's SQL Server.
- **Not included:** the course shortlist (an optional extra in our bid).

## Licence

The code in this repository is © Vanguard Digital Solutions. The Chelmsford College name, logos, photos and course data belong to the college and are not included in this repository.
