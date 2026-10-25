using System.Globalization;
using Chelmsford.Core.Payments;
using Chelmsford.Web.Models;
using Chelmsford.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Chelmsford.Web.Controllers;

/// <summary>
/// The payment journey: three ways in (student sign-in, payment link, guest lookup), then choose an
/// item, review, pay on the provider's hosted page and see confirmation once the payment is recorded.
/// </summary>
[Route("pay")]
public sealed class PayController(
    IPaymentService payments,
    IPaymentLinkService links,
    IPayableItemCatalogue catalogue,
    IPaymentGateway gateway,
    PayContextProtector contextProtector,
    ILogger<PayController> logger) : Controller
{
    private const string DemoApplicant = "APP-2027-00412";
    private const string DemoDepositName = "Creative industries course deposit";

    private static readonly DemoStudentChoice[] DemoStudents =
    [
        new("21004567", "Ellie Turner", "Fashion"),
        new("21004890", "Josh Patel", "Carpentry")
    ];

    private IActionResult PayView(string view, PayStepViewModel model)
    {
        if (string.IsNullOrEmpty(model.Title) || model.Title == "Chelmsford College")
        {
            model.Title = "Make a payment | Chelmsford College";
        }

        return View($"~/Views/Pay/{view}.cshtml", model);
    }

    private IActionResult Message(string heading, string? body, string? linkText = "Start again", string? linkUrl = "/make-a-payment/", int status = 200)
    {
        Response.StatusCode = status;
        return PayView("Message", new PayMessageViewModel
        {
            Title = heading + " | Chelmsford College",
            Heading = heading,
            Body = body,
            LinkText = linkText,
            LinkUrl = linkUrl
        });
    }

    private IActionResult SessionExpired() =>
        Message("Your session has ended", "For your security, we only keep your payment details for 30 minutes. Please start again.");

    // ---------- Route 1: student sign-in (demo) ----------

    [HttpGet("sign-in")]
    public IActionResult SignIn() => PayView("SignIn", new SignInViewModel
    {
        Title = "Sign in to pay | Chelmsford College",
        Step = 1,
        BackUrl = "/make-a-payment/",
        Students = DemoStudents
    });

    [HttpPost("sign-in")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignInPost([FromForm] string? studentId, CancellationToken cancellationToken)
    {
        var choice = DemoStudents.FirstOrDefault(s => s.StudentId == studentId);
        var subject = choice is null ? null : await payments.FindStudentAsync(choice.StudentId, cancellationToken);
        if (choice is null || subject is null)
        {
            var model = new SignInViewModel { Title = "Sign in to pay | Chelmsford College", Step = 1, BackUrl = "/make-a-payment/", Students = DemoStudents };
            model.Errors["student"] = "Choose a student to sign in as";
            return PayView("SignIn", model);
        }

        var context = new PayContext
        {
            Route = "student",
            SubjectKind = PaymentSubjectKind.Student,
            SubjectReference = subject.Reference,
            PayerType = PayerType.Student,
            PayerName = choice.Name,
            PayerEmail = subject.Email
        };
        contextProtector.Write(Response, context);
        return Redirect("/pay/items");
    }

    // ---------- Route 2: payment link ----------

    [HttpGet("link")]
    public IActionResult LinkStart() => PayView("LinkStart", new LinkStartViewModel
    {
        Title = "Pay with a payment link | Chelmsford College",
        Step = 1,
        BackUrl = "/make-a-payment/"
    });

    /// <summary>Demo only: issues a real, single-use 7-day link, as the admissions team would by email.</summary>
    [HttpPost("link/demo")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateDemoLink(CancellationToken cancellationToken)
    {
        var deposit = (await catalogue.GetAllAsync(cancellationToken))
            .FirstOrDefault(i => i.Name.Equals(DemoDepositName, StringComparison.OrdinalIgnoreCase))
            ?? (await catalogue.GetAllAsync(cancellationToken)).FirstOrDefault(i => i.Type == PayableItemType.CourseDeposit);
        if (deposit is null)
        {
            return Message("No deposit is set up", "Add a course deposit in the CMS under Payable items, then try again.");
        }

        var token = await links.CreateAsync(PaymentSubjectKind.Applicant, DemoApplicant, deposit.Key, TimeSpan.FromDays(7), cancellationToken);
        return Redirect("/pay/link/" + Uri.EscapeDataString(token));
    }

    [HttpGet("link/{token}")]
    public async Task<IActionResult> Link(string token, CancellationToken cancellationToken)
    {
        var resolved = await links.ResolveAsync(token, cancellationToken);
        if (resolved is null)
        {
            return LinkInvalid();
        }

        var context = new PayContext
        {
            Route = "link",
            SubjectKind = resolved.Subject.Kind,
            SubjectReference = resolved.Subject.Reference,
            PaymentLinkId = resolved.Link.Id
        };
        contextProtector.Write(Response, context);

        return PayView("Link", new LinkViewModel
        {
            Title = "Pay your " + resolved.Item.TypeName.ToLowerInvariant() + " | Chelmsford College",
            Step = 2,
            BackUrl = "/pay/link",
            Token = token,
            Link = resolved
        });
    }

    [HttpPost("link/{token}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LinkPost(
        string token,
        [FromForm] bool someoneElse,
        [FromForm] string? payerName,
        [FromForm] string? payerEmail,
        [FromForm] string? relationship,
        CancellationToken cancellationToken)
    {
        var resolved = await links.ResolveAsync(token, cancellationToken);
        if (resolved is null)
        {
            return LinkInvalid();
        }

        var context = contextProtector.Read(Request);
        if (context.PaymentLinkId != resolved.Link.Id)
        {
            context = new PayContext { Route = "link", SubjectKind = resolved.Subject.Kind, SubjectReference = resolved.Subject.Reference, PaymentLinkId = resolved.Link.Id };
        }

        Payer payer;
        if (someoneElse)
        {
            var model = new LinkViewModel
            {
                Title = "Pay your " + resolved.Item.TypeName.ToLowerInvariant() + " | Chelmsford College",
                Step = 2,
                BackUrl = "/pay/link",
                Token = token,
                Link = resolved,
                SomeoneElse = true,
                PayerName = payerName,
                PayerEmail = payerEmail,
                Relationship = relationship
            };
            if (string.IsNullOrWhiteSpace(payerName))
            {
                model.Errors["payerName"] = "Enter the full name of the person paying";
            }

            if (string.IsNullOrWhiteSpace(payerEmail))
            {
                model.Errors["payerEmail"] = "Enter the email address of the person paying";
            }
            else if (!BookingController.IsEmail(payerEmail))
            {
                model.Errors["payerEmail"] = "Enter an email address in the correct format, like name@example.com";
            }

            if (string.IsNullOrWhiteSpace(relationship) || !FindStudentViewModel.Relationships.Contains(relationship))
            {
                model.Errors["relationship"] = "Select your relationship to the applicant";
            }

            if (model.Errors.Count > 0)
            {
                return PayView("Link", model);
            }

            payer = new Payer(relationship is "Other" ? PayerType.Other : PayerType.ParentOrCarer, payerName!.Trim(), payerEmail!.Trim(), relationship);
        }
        else
        {
            payer = new Payer(PayerType.Applicant, resolved.Subject.FirstName, resolved.Subject.Email, null);
        }

        return await StartOrderAsync(context, resolved.Subject, resolved.Item.Key, payer, resolved.Link.Id, cancellationToken);
    }

    private IActionResult LinkInvalid() =>
        Message(
            "This payment link is not valid",
            "The link may have expired, already been used, or been copied incorrectly. Payment links last 7 days and can only be used once. Ask the college to send you a new one, or find the student instead.",
            "Find the student instead",
            "/pay/find-student",
            StatusCodes.Status404NotFound);

    // ---------- Route 3: guest lookup ----------

    [HttpGet("find-student")]
    public IActionResult FindStudent() => PayView("FindStudent", new FindStudentViewModel
    {
        Title = "Find the student | Chelmsford College",
        Step = 1,
        BackUrl = "/make-a-payment/"
    });

    [HttpPost("find-student")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FindStudentPost([FromForm] FindStudentForm form, CancellationToken cancellationToken)
    {
        var model = new FindStudentViewModel { Title = "Find the student | Chelmsford College", Step = 1, BackUrl = "/make-a-payment/", Form = form };
        var studentId = (form.StudentId ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (studentId.Length == 0)
        {
            model.Errors["StudentId"] = "Enter the student ID number";
        }
        else if (studentId.Length != 8 || !studentId.All(char.IsAsciiDigit))
        {
            model.Errors["StudentId"] = "Student ID number must be 8 digits";
        }

        DateOnly dob = default;
        var hasDay = int.TryParse(form.Day, NumberStyles.None, CultureInfo.InvariantCulture, out var day);
        var hasMonth = int.TryParse(form.Month, NumberStyles.None, CultureInfo.InvariantCulture, out var month);
        var hasYear = int.TryParse(form.Year, NumberStyles.None, CultureInfo.InvariantCulture, out var year);
        if (string.IsNullOrWhiteSpace(form.Day) && string.IsNullOrWhiteSpace(form.Month) && string.IsNullOrWhiteSpace(form.Year))
        {
            model.Errors["Day"] = "Enter the student's date of birth";
        }
        else if (!hasDay || !hasMonth || !hasYear || year < 1900 || year > DateTime.UtcNow.Year
                 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            model.Errors["Day"] = "Date of birth must be a real date, for example 27 3 2009";
        }
        else
        {
            dob = new DateOnly(year, month, day);
            if (dob > DateOnly.FromDateTime(DateTime.UtcNow))
            {
                model.Errors["Day"] = "Date of birth must be in the past";
            }
        }

        if (string.IsNullOrWhiteSpace(form.PayerName))
        {
            model.Errors["PayerName"] = "Enter your full name";
        }

        if (string.IsNullOrWhiteSpace(form.PayerEmail))
        {
            model.Errors["PayerEmail"] = "Enter your email address";
        }
        else if (!BookingController.IsEmail(form.PayerEmail))
        {
            model.Errors["PayerEmail"] = "Enter an email address in the correct format, like name@example.com";
        }

        if (string.IsNullOrWhiteSpace(form.Relationship) || !FindStudentViewModel.Relationships.Contains(form.Relationship))
        {
            model.Errors["Relationship"] = "Select your relationship to the student";
        }

        if (model.Errors.Count > 0)
        {
            return PayView("FindStudent", model);
        }

        var clientKey = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await payments.GuestLookupAsync(studentId, dob, clientKey, cancellationToken);
        switch (result.Outcome)
        {
            case GuestLookupOutcome.TooManyAttempts:
                model.Errors["StudentId"] = "Too many attempts. Try again in 15 minutes.";
                return PayView("FindStudent", model);
            case GuestLookupOutcome.NotFound or not GuestLookupOutcome.Found:
                model.Errors["StudentId"] = result.AttemptsLeft == 1
                    ? "We could not find a student with that ID number and date of birth. Check both and try again. You have 1 more attempt."
                    : $"We could not find a student with that ID number and date of birth. Check both and try again. You have {result.AttemptsLeft} more attempts.";
                return PayView("FindStudent", model);
        }

        var subject = result.Subject!;
        contextProtector.Write(Response, new PayContext
        {
            Route = "guest",
            SubjectKind = subject.Kind,
            SubjectReference = subject.Reference,
            PayerType = form.Relationship == "Other" ? PayerType.Other : PayerType.ParentOrCarer,
            PayerName = form.PayerName!.Trim(),
            PayerEmail = form.PayerEmail!.Trim(),
            Relationship = form.Relationship
        });
        logger.LogInformation("Guest lookup succeeded; payer is the student's {Relationship}", form.Relationship);
        return Redirect("/pay/items");
    }

    // ---------- Choose an item ----------

    [HttpGet("items")]
    public async Task<IActionResult> Items(CancellationToken cancellationToken)
    {
        var context = contextProtector.Read(Request);
        var subject = context.HasSubject ? await payments.FindSubjectAsync(context.SubjectKind!.Value, context.SubjectReference!, cancellationToken) : null;
        if (subject is null || !context.HasPayer)
        {
            return SessionExpired();
        }

        return PayView("Items", await ItemsModelAsync(context, subject, cancellationToken));
    }

    private async Task<ItemsViewModel> ItemsModelAsync(PayContext context, PaymentSubject subject, CancellationToken cancellationToken) => new()
    {
        Title = "Choose a payment | Chelmsford College",
        Step = 2,
        BackUrl = context.Route == "student" ? "/pay/sign-in" : "/pay/find-student",
        Subject = subject,
        Items = await payments.GetDueItemsAsync(subject, cancellationToken)
    };

    [HttpPost("items")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ItemsPost([FromForm] Guid? item, CancellationToken cancellationToken)
    {
        var context = contextProtector.Read(Request);
        var subject = context.HasSubject ? await payments.FindSubjectAsync(context.SubjectKind!.Value, context.SubjectReference!, cancellationToken) : null;
        if (subject is null || !context.HasPayer)
        {
            return SessionExpired();
        }

        if (item is null)
        {
            var model = await ItemsModelAsync(context, subject, cancellationToken);
            model.Errors["item"] = "Select the payment you want to make";
            return PayView("Items", model);
        }

        return await StartOrderAsync(context, subject, item.Value, context.ToPayer(), context.PaymentLinkId, cancellationToken);
    }

    private async Task<IActionResult> StartOrderAsync(PayContext context, PaymentSubject subject, Guid itemKey, Payer payer, Guid? linkId, CancellationToken cancellationToken)
    {
        var result = await payments.StartOrderAsync(subject, itemKey, payer, linkId, cancellationToken);
        switch (result.Outcome)
        {
            case StartOrderOutcome.Started when result.Order is not null:
                context.PayerType = payer.Type;
                context.PayerName = payer.Name;
                context.PayerEmail = payer.Email;
                context.Relationship = payer.Relationship;
                context.AddOrder(result.Order.Reference);
                contextProtector.Write(Response, context);
                return Redirect("/pay/review/" + Uri.EscapeDataString(result.Order.Reference));
            case StartOrderOutcome.AlreadyPaid:
                return Message("This has already been paid",
                    "There's nothing more to pay for this item. If you think this is wrong, contact the college's finance team.",
                    context.Route == "link" ? "Make a payment" : "Choose another payment",
                    context.Route == "link" ? "/make-a-payment/" : "/pay/items");
            case StartOrderOutcome.SoldOut:
                return Message("Sorry, this trip is full",
                    "All the places on this trip have been taken. Ask the student's tutor whether a waiting list is open.",
                    "Choose another payment", "/pay/items");
            default:
                return Message("This payment is not available",
                    "It may have been withdrawn, or it may not apply to this student's course.",
                    "Choose another payment", "/pay/items");
        }
    }

    // ---------- Review, checkout, confirmation ----------

    private async Task<(PayContext Context, PaymentOrder? Order)> OwnedOrderAsync(string reference, CancellationToken cancellationToken)
    {
        var context = contextProtector.Read(Request);
        if (!context.OwnsOrder(reference))
        {
            return (context, null);
        }

        return (context, await payments.GetOrderAsync(reference, cancellationToken));
    }

    /// <summary>
    /// Only a Pending order can be reviewed or sent to checkout. A finished order goes to its
    /// confirmation page; any other order gets a message that says how to start again.
    /// </summary>
    private IActionResult? NotPending(PaymentOrder order)
    {
        if (order.Status == OrderStatus.Pending)
        {
            return null;
        }

        if (order.Status is OrderStatus.Paid or OrderStatus.PaidDuplicate or OrderStatus.Refunded)
        {
            return Redirect("/pay/complete/" + Uri.EscapeDataString(order.Reference));
        }

        return Message("This payment is no longer open", "If you don't finish paying within 30 minutes, start again from the payment page.");
    }

    [HttpGet("review/{reference}")]
    public async Task<IActionResult> Review(string reference, CancellationToken cancellationToken)
    {
        var (context, order) = await OwnedOrderAsync(reference, cancellationToken);
        if (order is null)
        {
            return SessionExpired();
        }

        if (NotPending(order) is { } notPending)
        {
            return notPending;
        }

        return PayView("Review", new ReviewViewModel
        {
            Title = "Check and pay | Chelmsford College",
            Step = 3,
            BackUrl = context.Route == "link" ? null : "/pay/items",
            Order = order
        });
    }

    [HttpPost("checkout/{reference}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Checkout(string reference, CancellationToken cancellationToken)
    {
        var (_, order) = await OwnedOrderAsync(reference, cancellationToken);
        if (order is null)
        {
            return SessionExpired();
        }

        if (NotPending(order) is { } notPending)
        {
            return notPending;
        }

        var origin = $"{Request.Scheme}://{Request.Host}";
        var escaped = Uri.EscapeDataString(order.Reference);
        try
        {
            var checkout = await payments.BeginCheckoutAsync(order.Reference, new CheckoutUrls(origin + "/pay/complete/" + escaped, origin + "/pay/review/" + escaped), cancellationToken);
            return Redirect(checkout.RedirectUrl);
        }
        catch (PaymentGatewayException ex)
        {
            // The provider could not be reached or refused to start a checkout. Nothing has been taken.
            logger.LogWarning(ex, "The payment provider could not start checkout for {Reference}", order.Reference);
            return Message(
                "We could not reach the payment provider",
                "Nothing has been charged. Please try again in a few minutes.",
                "Back to your payment",
                "/pay/review/" + escaped,
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Checkout could not start for {Reference}", order.Reference);
            return Message("We could not start the payment", "This payment can no longer be taken. Please start again.");
        }
    }

    [HttpGet("test-checkout/{reference}")]
    public async Task<IActionResult> TestCheckout(string reference, CancellationToken cancellationToken)
    {
        if (!string.Equals(gateway.Name, "Demo", StringComparison.Ordinal))
        {
            return SiteResults.NotFoundView(this);
        }

        var (_, order) = await OwnedOrderAsync(reference, cancellationToken);
        if (order is null)
        {
            return SessionExpired();
        }

        if (NotPending(order) is { } notPending)
        {
            return notPending;
        }

        return View("~/Views/Pay/TestCheckout.cshtml", new TestCheckoutViewModel
        {
            Title = "Test checkout | Chelmsford College",
            Order = order
        });
    }

    [HttpPost("test-checkout/{reference}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestCheckoutPost(string reference, CancellationToken cancellationToken)
    {
        if (!string.Equals(gateway.Name, "Demo", StringComparison.Ordinal))
        {
            return SiteResults.NotFoundView(this);
        }

        var (_, order) = await OwnedOrderAsync(reference, cancellationToken);
        if (order is null)
        {
            return SessionExpired();
        }

        // The same rule as the review page: only an order that is still Pending can be paid.
        if (NotPending(order) is { } notPending)
        {
            return notPending;
        }

        await payments.MarkPaidAsync(order.Id, "demo_pay_" + Guid.NewGuid().ToString("N")[..12], cancellationToken);
        return Redirect("/pay/complete/" + Uri.EscapeDataString(order.Reference));
    }

    [HttpGet("complete/{reference}")]
    public async Task<IActionResult> Complete(string reference, CancellationToken cancellationToken)
    {
        var context = contextProtector.Read(Request);
        if (!context.OwnsOrder(reference))
        {
            return SessionExpired();
        }

        var order = await payments.ConfirmReturnAsync(reference, cancellationToken);
        if (order is null)
        {
            return SessionExpired();
        }

        var paid = order.Status == OrderStatus.Paid;
        var duplicate = order.Status == OrderStatus.PaidDuplicate;
        var refunded = order.Status == OrderStatus.Refunded;
        var finished = paid || duplicate || refunded;

        string? receiptTo = order.PayerEmail;
        if (paid && order.PayerType is PayerType.ParentOrCarer or PayerType.Other)
        {
            receiptTo = $"{order.PayerEmail}, and a copy to {order.SubjectFirstName}";
        }

        if (finished)
        {
            // The payment is done, so nothing about who paid for whom needs to stay in the cookie. Only
            // the references of finished orders are kept, so this page can be reloaded.
            await ClearJourneyAsync(context, cancellationToken);
        }

        var title = paid ? "Payment received"
            : duplicate ? "This item had already been paid"
            : refunded ? "Payment refunded"
            : "We're confirming your payment";
        return PayView("Complete", new CompleteViewModel
        {
            Title = title + " | Chelmsford College",
            Step = finished ? 0 : 3,
            Order = order,
            ReceiptTo = receiptTo,
            Duplicate = duplicate,
            Refunded = refunded
        });
    }

    /// <summary>
    /// Forgets the payer and the student or applicant, and keeps only the references of orders that
    /// have finished (so a confirmation page can still be reloaded).
    /// </summary>
    private async Task ClearJourneyAsync(PayContext context, CancellationToken cancellationToken)
    {
        var finishedReferences = new List<string>();
        foreach (var reference in context.Orders)
        {
            var order = await payments.GetOrderAsync(reference, cancellationToken);
            if (order is { Status: OrderStatus.Paid or OrderStatus.PaidDuplicate or OrderStatus.Refunded })
            {
                finishedReferences.Add(reference);
            }
        }

        contextProtector.Write(Response, new PayContext { Orders = finishedReferences });
    }

    /// <summary>Stripe webhook. Signature checked by the handler; events are deduplicated by ID.</summary>
    [HttpPost("webhook/stripe")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> StripeWebhook([FromServices] IPaymentWebhookHandler handler, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        var outcome = await handler.HandleAsync(payload, Request.Headers["Stripe-Signature"].ToString(), cancellationToken);
        return outcome == WebhookOutcome.InvalidSignature ? BadRequest() : Ok();
    }
}
