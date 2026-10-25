# Chelmsford College website demo: plan

A working demo for the Chelmsford College website tender (demo brief, Appendix 1). It follows the architecture in our bid answers (Appendix 4) so the panel sees the real approach, not a mock-up.

Design: Claude Design canvas "Chelmsford College demo site" (mobile first, then desktop).

## What the brief asks for, and where it lives

| Brief requirement | Where it is in the demo |
|---|---|
| Homepage: logo and main navigation | `Views/Shared/_Layout.cshtml`, `Views/Home.cshtml` |
| Prominent course search, with a results page filtered by subject, level, study mode and campus | Home search box → `/courses?q=&subject=&level=&mode=&campus=&funding=` (`CourseListingController`) |
| Prominent area for key messages and campaigns | Campaign nodes in the CMS with start/end dates. The homepage shows the one that is live now |
| Areas for video and images | Homepage video block (YouTube/Vimeo link from the CMS, click-to-load), photo cards |
| Course page from the SQL database, one template for every course | Integration layer syncs the college tables → site database → one `Course` template |
| Course page: header, course image, easy to scan on mobile | Header, image, "At a glance", jump links, expandable sections, sticky "Apply now" |
| Events listing with filters (date, type) | `/events?type=&month=` |
| Single event: date, time, location, description, how to book | Event template, booking form, add-to-calendar (.ics) |
| Payments: deposits, materials fees, trips, one-offs; paid by students or parents/carers | `/make-a-payment`: student sign-in, secure payment link, guest lookup; Stripe hosted checkout |
| Mobile first, WCAG 2.2 AA | Mobile-first CSS, skip link, focus styles, reduced motion, real form labels and hints, 44px+ targets |
| Show the CMS | Umbraco 17 back office at `/umbraco` |
| Panel access after the session | Deployed to `https://chelmsford.vanguarddigitalsolutions.co.uk` |

## Commitments from our bid answers that the demo must keep

- **Platform:** Umbraco 17 LTS on ASP.NET Core / .NET 10. Pages are rendered on the server.
- **Integration layer:**
  - Reads the college's existing tables through a read-only connection and never writes to them.
  - Cleans the data, then keeps the website's own database in sync every few minutes.
  - Visitors are served from the website's copy, so if the source is unavailable the site keeps running on the last synced data.
- **Data quirks we found and handle:**
  - Raw HTML in fields, with Bootstrap 3 glyphicon buttons.
  - The text `'NULL'` used instead of real nulls.
  - `_x000D_` carriage-return artefacts.
  - Fees held as free text.
  - Levels and funding only reachable through link tables.
- **Campus and study mode:**
  - The demo data has no campus field, and every course is full-time.
  - Campus comes from a clearly labelled demo mapping (subject area → campus) until the real source is confirmed in discovery.
- **Course page structure:** At a glance → Overview → What you'll study → How you'll be assessed → Entry requirements → Fees, funding and additional costs → Where it can lead.
- **Marketing can enrich courses:**
  - Every synced course has a CMS node, where editors add an image, a video and a student story.
  - The synced facts are read-only, so editors can't overwrite them by mistake.
- **Payments:**
  - Payable items are created by staff in the CMS. Each has a type (deposit, materials fee, trip or one-off), the course(s) it applies to, an amount, a deadline, places (for trips) and a cost code.
  - Every payment is linked to who it is for, what it is for and who paid.
  - Three ways to pay:
    - Student sign-in (Entra ID in production; a labelled demo sign-in here).
    - Secure single-use payment links that expire.
    - A guest page using student ID and date of birth. It shows only the first name and course, limits repeated failed lookups, and records the payer's name, email and relationship.
- **Exactly-once payments:**
  - The order is created before checkout with a unique key, so a refresh returns the same order.
  - An item that has already been paid can't be paid again.
  - Webhook events are deduplicated by event ID.
  - Confirmation is shown only after the payment is recorded.
  - Trip places are reserved inside a transaction.
- **Stripe hosted checkout:**
  - Card details never touch our server.
  - Metadata on each payment carries the student ID, course code, item and cost code.
  - With no Stripe keys configured, a clearly labelled test checkout is used instead.
- **Data held off the web server:**
  - Databases and media live on a mounted volume, not in the container.
  - The media path is configurable.
- **Structured data:** JSON-LD for `Course` and `Event`.
- **Accessible cookie banner:** analytics run only after consent.
- **Not in the demo (optional extras in the bid):** course shortlist.

## Architecture

```
src/
  Chelmsford.Core/            domain models, interfaces, pure logic (cleaning, mapping, payment rules)
  Chelmsford.Infrastructure/  EF Core site database, source reader, sync, Stripe and demo gateways
  Chelmsford.Web/             Umbraco 17 site: doc types, content seeding, controllers, Razor views
tests/
  Chelmsford.Tests/           xUnit: cleaner, mapping, payment idempotency, guest lookup limits
deploy/                       Caddy block and deployment notes
```

- **College source database (stand-in for their SQL Server):**
  - A SQLite file built once from the supplied spreadsheet, keeping its quirks.
  - Tables use the college's own names (`courses`, `course_sections`, `course_levels`, `course_level_link`, `course_funding`, `course_fund_link`), plus demo `students` and `applications` tables for the payment journeys.
  - It is opened read-only by the integration layer.
- **Site database:**
  - EF Core tables for the synced courses, sync runs, payable-item state, orders, payment links, webhook events, event bookings and the email outbox.
  - In production these tables live in the website's SQL Server database. For the demo they are in a separate SQLite file next to Umbraco's, to avoid two ORMs locking one SQLite file.
- **Umbraco:**
  - Content types are created in code at startup, so the site builds from an empty database in the container.
  - Routing uses hijacked controllers per document type, returning explicit views.
  - Sample content is seeded on first run: campaigns, events, payable items and media from `college-content/images`.

## College assets stay out of git

The repo is public. The college's logos, photos and course spreadsheet came in a tender pack, so they live in `college-content/` (gitignored) and are mounted into the container at run time. The site falls back to a text wordmark if they are missing.

## Build order

1. Scaffold the solution (Umbraco 17.7.1, .NET 10), the projects, packages and config.
2. Core: models, HTML cleaner, NULL handling, level and funding mapping, plus tests.
3. Source database seeder (spreadsheet → JSON → SQLite) and the integration sync service.
4. Umbraco schema (data types, document types) and content seeding.
5. Layout, design tokens and accessibility basics.
6. Homepage, course search, course page.
7. Events listing, event page, booking, .ics file.
8. Payments: items, three routes, orders, Stripe and demo gateways, webhook, confirmation.
9. "How the data gets here" page for the panel: raw vs cleaned, last sync.
10. Container, compose, Caddy, deployment notes.
11. Check: build, tests, run the container, click through every journey, keyboard and contrast checks.
