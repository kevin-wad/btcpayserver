using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Filters;
using BTCPayServer.Models;
using BTCPayServer.Models.PaymentRequestViewModels;
using BTCPayServer.PaymentRequest;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Rating;
using BTCPayServer.Security;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.PaymentRequests;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using Ganss.XSS;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using NBitpayClient;

namespace BTCPayServer.Controllers
{
    [Route("payment-requests")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public class PaymentRequestController : Controller
    {
        private readonly InvoiceController _InvoiceController;
        private readonly UserManager<ApplicationUser> _UserManager;
        private readonly StoreRepository _StoreRepository;
        private readonly PaymentRequestRepository _PaymentRequestRepository;
        private readonly PaymentRequestService _PaymentRequestService;
        private readonly EventAggregator _EventAggregator;
        private readonly CurrencyNameTable _Currencies;
        private readonly InvoiceRepository _InvoiceRepository;
        private readonly LinkGenerator _linkGenerator;

        public PaymentRequestController(
            InvoiceController invoiceController,
            UserManager<ApplicationUser> userManager,
            StoreRepository storeRepository,
            PaymentRequestRepository paymentRequestRepository,
            PaymentRequestService paymentRequestService,
            EventAggregator eventAggregator,
            CurrencyNameTable currencies,
            InvoiceRepository invoiceRepository,
            LinkGenerator linkGenerator)
        {
            _InvoiceController = invoiceController;
            _UserManager = userManager;
            _StoreRepository = storeRepository;
            _PaymentRequestRepository = paymentRequestRepository;
            _PaymentRequestService = paymentRequestService;
            _EventAggregator = eventAggregator;
            _Currencies = currencies;
            _InvoiceRepository = invoiceRepository;
            _linkGenerator = linkGenerator;
        }

        [HttpGet]
        [Route("")]
        [BitpayAPIConstraint(false)]
        public async Task<IActionResult> GetPaymentRequests(int skip = 0, int count = 50, bool includeArchived = false)
        {
            var result = await _PaymentRequestRepository.FindPaymentRequests(new PaymentRequestQuery()
            {
                UserId = GetUserId(), Skip = skip, Count = count, IncludeArchived = includeArchived
            });
            return View(new ListPaymentRequestsViewModel()
            {
                IncludeArchived = includeArchived,
                Skip = skip,
                Count = count,
                Total = result.Total,
                Items = result.Items.Select(data => new ViewPaymentRequestViewModel(data)).ToList()
            });
        }

        [HttpGet]
        [Route("edit/{id?}")]
        public async Task<IActionResult> EditPaymentRequest(string id)
        {
            SelectList stores = null;
            var data = await _PaymentRequestRepository.FindPaymentRequest(id, GetUserId());
            if (data == null && !string.IsNullOrEmpty(id))
            {
                return NotFound();
            }

            stores = new SelectList(await _StoreRepository.GetStoresByUserId(GetUserId()), nameof(StoreData.Id),
                nameof(StoreData.StoreName), data?.StoreDataId);
            if (!stores.Any())
            {
                TempData.SetStatusMessageModel(new StatusMessageModel()
                {
                    Html =
                        $"Error: You need to create at least one store. <a href='{Url.Action("CreateStore", "UserStores")}' class='alert-link'>Create store</a>",
                    Severity = StatusMessageModel.StatusSeverity.Error
                });
                return RedirectToAction("GetPaymentRequests");
            }

            return View(nameof(EditPaymentRequest), new UpdatePaymentRequestViewModel(data) {Stores = stores});
        }

        [HttpPost]
        [Route("edit/{id?}")]
        public async Task<IActionResult> EditPaymentRequest(string id, UpdatePaymentRequestViewModel viewModel)
        {
            if (string.IsNullOrEmpty(viewModel.Currency) ||
                _Currencies.GetCurrencyData(viewModel.Currency, false) == null)
                ModelState.AddModelError(nameof(viewModel.Currency), "Invalid currency");

            var data = await _PaymentRequestRepository.FindPaymentRequest(id, GetUserId());
            if (data == null && !string.IsNullOrEmpty(id))
            {
                return NotFound();
            }

            if ( data?.Archived is true && viewModel?.Archived is true)
            {
                ModelState.AddModelError(string.Empty, "You cannot edit an archived payment request.");
            }

            if (!ModelState.IsValid)
            {
                viewModel.Stores = new SelectList(await _StoreRepository.GetStoresByUserId(GetUserId()),
                    nameof(StoreData.Id),
                    nameof(StoreData.StoreName), data?.StoreDataId);

                return View(nameof(EditPaymentRequest),viewModel);
            }

            if (data == null)
            {
                data = new PaymentRequestData();
            }

            data.StoreDataId = viewModel.StoreId;
            data.Archived = viewModel.Archived;
            var blob = data.GetBlob();

            blob.Title = viewModel.Title;
            blob.Email = viewModel.Email;
            blob.Description = viewModel.Description;
            blob.Amount = viewModel.Amount;
            blob.ExpiryDate = viewModel.ExpiryDate?.ToUniversalTime();
            blob.Currency = viewModel.Currency;
            blob.EmbeddedCSS = viewModel.EmbeddedCSS;
            blob.CustomCSSLink = viewModel.CustomCSSLink;
            blob.AllowCustomPaymentAmounts = viewModel.AllowCustomPaymentAmounts;

            data.SetBlob(blob);
            if (string.IsNullOrEmpty(id))
            {
                data.Created = DateTimeOffset.UtcNow;
            }

            data = await _PaymentRequestRepository.CreateOrUpdatePaymentRequest(data);
            _EventAggregator.Publish(new PaymentRequestUpdated() {Data = data, PaymentRequestId = data.Id,});

            TempData[WellKnownTempData.SuccessMessage] = "Saved";
            return RedirectToAction(nameof(EditPaymentRequest), new {id = data.Id});
        }

        [HttpGet]
        [Route("{id}")]
        [AllowAnonymous]
        public async Task<IActionResult> ViewPaymentRequest(string id)
        {
            var result = await _PaymentRequestService.GetPaymentRequest(id, GetUserId());
            if (result == null)
            {
                return NotFound();
            }

            result.HubPath = PaymentRequestHub.GetHubPath(this.Request);
            return View(result);
        }

        [HttpGet]
        [Route("{id}/pay")]
        [AllowAnonymous]
        public async Task<IActionResult> PayPaymentRequest(string id, bool redirectToInvoice = true,
            decimal? amount = null, CancellationToken cancellationToken = default)
        {
            if (amount.HasValue && amount.Value <= 0)
            {
                return BadRequest("Please provide an amount greater than 0");
            }

            var result = await _PaymentRequestService.GetPaymentRequest(id, GetUserId());
            if (result == null)
            {
                return NotFound();
            }

            if (result.Archived)
            {
                if (redirectToInvoice)
                {
                    return RedirectToAction("ViewPaymentRequest", new {Id = id});
                }

                return BadRequest("Payment Request cannot be paid as it has been archived");
            }

            result.HubPath = PaymentRequestHub.GetHubPath(this.Request);
            if (result.AmountDue <= 0)
            {
                if (redirectToInvoice)
                {
                    return RedirectToAction("ViewPaymentRequest", new {Id = id});
                }

                return BadRequest("Payment Request has already been settled.");
            }

            if (result.ExpiryDate.HasValue && DateTime.Now >= result.ExpiryDate)
            {
                if (redirectToInvoice)
                {
                    return RedirectToAction("ViewPaymentRequest", new {Id = id});
                }

                return BadRequest("Payment Request has expired");
            }

            var statusesAllowedToDisplay = new List<InvoiceStatus>() {InvoiceStatus.New};
            var validInvoice = result.Invoices.FirstOrDefault(invoice =>
                Enum.TryParse<InvoiceStatus>(invoice.Status, true, out var status) &&
                statusesAllowedToDisplay.Contains(status));

            if (validInvoice != null)
            {
                if (redirectToInvoice)
                {
                    return RedirectToAction("Checkout", "Invoice", new {Id = validInvoice.Id});
                }

                return Ok(validInvoice.Id);
            }

            if (result.AllowCustomPaymentAmounts && amount != null)
                amount = Math.Min(result.AmountDue, amount.Value);
            else
                amount = result.AmountDue;


            var pr = await _PaymentRequestRepository.FindPaymentRequest(id, null, cancellationToken);
            var blob = pr.GetBlob();
            var store = pr.StoreData;
            try
            {
                var redirectUrl = _linkGenerator.PaymentRequestLink(id, Request.Scheme, Request.Host, Request.PathBase);
                var newInvoiceId = (await _InvoiceController.CreateInvoiceCore(new CreateInvoiceRequest()
                        {
                            OrderId = $"{PaymentRequestRepository.GetOrderIdForPaymentRequest(id)}",
                            Currency = blob.Currency,
                            Price = amount.Value,
                            FullNotifications = true,
                            BuyerEmail = result.Email,
                            RedirectURL = redirectUrl,
                        }, store, HttpContext.Request.GetAbsoluteRoot(),
                        new List<string>() {PaymentRequestRepository.GetInternalTag(id)},
                        cancellationToken: cancellationToken))
                    .Data.Id;

                if (redirectToInvoice)
                {
                    return RedirectToAction("Checkout", "Invoice", new {Id = newInvoiceId});
                }

                return Ok(newInvoiceId);
            }
            catch (BitpayHttpException e)
            {
                return BadRequest(e.Message);
            }
        }

        [HttpGet]
        [Route("{id}/cancel")]
        public async Task<IActionResult> CancelUnpaidPendingInvoice(string id, bool redirect = true)
        {
            var result = await _PaymentRequestService.GetPaymentRequest(id, GetUserId());
            if (result == null)
            {
                return NotFound();
            }

            var invoice = result.Invoices.SingleOrDefault(requestInvoice =>
                requestInvoice.Status.Equals(InvoiceState.ToString(InvoiceStatus.New),
                    StringComparison.InvariantCulture) && !requestInvoice.Payments.Any());

            if (invoice == null)
            {
                return BadRequest("No unpaid pending invoice to cancel");
            }

            await _InvoiceRepository.UpdatePaidInvoiceToInvalid(invoice.Id);
            _EventAggregator.Publish(new InvoiceEvent(await _InvoiceRepository.GetInvoice(invoice.Id), 1008,
                InvoiceEvent.MarkedInvalid));

            if (redirect)
            {
                TempData[WellKnownTempData.SuccessMessage] = "Payment cancelled";
                return RedirectToAction(nameof(ViewPaymentRequest), new {Id = id});
            }

            return Ok("Payment cancelled");
        }

        private string GetUserId()
        {
            return _UserManager.GetUserId(User);
        }

        [HttpGet]
        [Route("{id}/clone")]
        public async Task<IActionResult> ClonePaymentRequest(string id)
        {
            var result = await EditPaymentRequest(id);
            if (result is ViewResult viewResult)
            {
                var model = (UpdatePaymentRequestViewModel)viewResult.Model;
                model.Id = null;
                model.Archived = false;
                model.Title = $"Clone of {model.Title}";
                return View("EditPaymentRequest", model);
            }

            return NotFound();
        }

        [HttpGet("{id}/archive")]
        public async Task<IActionResult> TogglePaymentRequestArchival(string id)
        {
            var result = await EditPaymentRequest(id);
            if (result is ViewResult viewResult)
            {
                var model = (UpdatePaymentRequestViewModel)viewResult.Model;
                model.Archived = !model.Archived;
                result = await EditPaymentRequest(id, model);
                TempData[WellKnownTempData.SuccessMessage] = model.Archived
                    ? "The payment request has been archived and will no longer appear in the  payment request list by default again."
                    : "The payment request has been unarchived and will appear in the  payment request list by default.";
                return result;
            }

            return NotFound();
        }
    }
}
