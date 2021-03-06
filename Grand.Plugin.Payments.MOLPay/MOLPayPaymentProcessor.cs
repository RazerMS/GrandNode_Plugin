using Grand.Core;
using Grand.Core.Domain.Directory;
using Grand.Core.Domain.Orders;
using Grand.Core.Domain.Shipping;
using Grand.Core.Domain.Payments;
using Grand.Core.Infrastructure;
using Grand.Core.Plugins;
using Grand.Plugin.Payments.MOLPay.Controllers;
using Grand.Services.Catalog;
using Grand.Services.Common;
using Grand.Services.Configuration;
using Grand.Services.Customers;
using Grand.Services.Directory;
using Grand.Services.Localization;
using Grand.Services.Orders;
using Grand.Services.Payments;
using Grand.Services.Tax;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Grand.Plugin.Payments.MOLPay
{
    /// <summary>
    /// MOLPay payment processor
    /// </summary>
    public class MOLPayPaymentProcessor : BasePlugin, IPaymentMethod
    {
        #region Fields

        private readonly CurrencySettings _currencySettings;
        private readonly ICheckoutAttributeParser _checkoutAttributeParser;
        private readonly ICurrencyService _currencyService;
        private readonly IGenericAttributeService _genericAttributeService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILocalizationService _localizationService;
        private readonly IOrderTotalCalculationService _orderTotalCalculationService;
        private readonly ISettingService _settingService;
        private readonly ITaxService _taxService;
        private readonly IProductService _productService;
        private readonly IWebHelper _webHelper;
        private readonly MOLPayPaymentSettings _molPayPaymentSettings;

        #endregion

        #region Ctor

        public MOLPayPaymentProcessor(CurrencySettings currencySettings,
            ICheckoutAttributeParser checkoutAttributeParser,
            ICurrencyService currencyService,
            IGenericAttributeService genericAttributeService,
            IHttpContextAccessor httpContextAccessor,
            ILocalizationService localizationService,
            IOrderTotalCalculationService orderTotalCalculationService,
            ISettingService settingService,
            ITaxService taxService,
            IProductService productService,
            IWebHelper webHelper,
            MOLPayPaymentSettings molPayPaymentSettings)
        {
            this._currencySettings = currencySettings;
            this._checkoutAttributeParser = checkoutAttributeParser;
            this._currencyService = currencyService;
            this._genericAttributeService = genericAttributeService;
            this._httpContextAccessor = httpContextAccessor;
            this._localizationService = localizationService;
            this._orderTotalCalculationService = orderTotalCalculationService;
            this._settingService = settingService;
            this._taxService = taxService;
            this._productService = productService;
            this._webHelper = webHelper;
            this._molPayPaymentSettings = molPayPaymentSettings;
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Gets MOLPay URL
        /// </summary>
        /// <returns></returns>
        private string GetMOLPayUrl()
        {
            return _molPayPaymentSettings.UseSandbox ?
                "https://sandbox.molpay.com/MOLPay/pay/" :
                "https://www.onlinepayment.com.my/MOLPay/pay";
        }

        private string GetIpnMOLPayUrl()
        {
            return _molPayPaymentSettings.UseSandbox ?
                "https://ipnpb.sandbox.paypal.com/cgi-bin/webscr" :
                "https://ipnpb.paypal.com/cgi-bin/webscr";
        }

        /// <summary>
        /// Gets PDT details
        /// </summary>
        /// <param name="tx">TX</param>
        /// <param name="values">Values</param>
        /// <param name="response">Response</param>
        /// <returns>Result</returns>
        public bool GetPdtDetails(string tx, out Dictionary<string, string> values, out string response)
        {
            var req = (HttpWebRequest)WebRequest.Create(GetMOLPayUrl());
            req.Method = WebRequestMethods.Http.Post;
            req.ContentType = "application/x-www-form-urlencoded";
            //now MOLPay requires user-agent. otherwise, we can get 403 error
            req.UserAgent = _httpContextAccessor.HttpContext.Request.Headers[HeaderNames.UserAgent];

            var formContent = $"cmd=_notify-synch&at={_molPayPaymentSettings.PdtToken}&tx={tx}";
            req.ContentLength = formContent.Length;

            using (var sw = new StreamWriter(req.GetRequestStream(), Encoding.ASCII))
                sw.Write(formContent);

            using (var sr = new StreamReader(req.GetResponse().GetResponseStream()))
                response = WebUtility.UrlDecode(sr.ReadToEnd());

            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool firstLine = true, success = false;
            foreach (var l in response.Split('\n'))
            {
                var line = l.Trim();
                if (firstLine)
                {
                    success = line.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase);
                    firstLine = false;
                }
                else
                {
                    var equalPox = line.IndexOf('=');
                    if (equalPox >= 0)
                        values.Add(line.Substring(0, equalPox), line.Substring(equalPox + 1));
                }
            }

            return success;
        }

        /// <summary>
        /// Verifies IPN
        /// </summary>
        /// <param name="formString">Form string</param>
        /// <param name="values">Values</param>
        /// <returns>Result</returns>
        public bool VerifyIpn(string formString, out Dictionary<string, string> values)
        {
            var req = (HttpWebRequest)WebRequest.Create(GetIpnMOLPayUrl());
            req.Method = WebRequestMethods.Http.Post;
            req.ContentType = "application/x-www-form-urlencoded";
            //now MOLPay requires user-agent. otherwise, we can get 403 error
            req.UserAgent = _httpContextAccessor.HttpContext.Request.Headers[HeaderNames.UserAgent];

            var formContent = $"cmd=_notify-validate&{formString}";
            req.ContentLength = formContent.Length;

            using (var sw = new StreamWriter(req.GetRequestStream(), Encoding.ASCII))
            {
                sw.Write(formContent);
            }

            string response;
            using (var sr = new StreamReader(req.GetResponse().GetResponseStream()))
            {
                response = WebUtility.UrlDecode(sr.ReadToEnd());
            }
            var success = response.Trim().Equals("VERIFIED", StringComparison.OrdinalIgnoreCase);

            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in formString.Split('&'))
            {
                var line = l.Trim();
                var equalPox = line.IndexOf('=');
                if (equalPox >= 0)
                    values.Add(line.Substring(0, equalPox), line.Substring(equalPox + 1));
            }

            return success;
        }

        /// <summary>
        /// Create common query parameters for the request
        /// </summary>
        /// <param name="postProcessPaymentRequest">Payment info required for an order processing</param>
        /// <returns>Created query parameters</returns>
        private IDictionary<string, string> CreateQueryParameters(PostProcessPaymentRequest postProcessPaymentRequest)
        {
            //get store location
            var storeLocation = _webHelper.GetStoreLocation();
            var stateProvince = "";
            var countryCode = "";
            if (!String.IsNullOrEmpty(postProcessPaymentRequest.Order.ShippingAddress?.StateProvinceId))
            {
                var state = EngineContext.Current.Resolve<IStateProvinceService>().GetStateProvinceById(postProcessPaymentRequest.Order.ShippingAddress?.StateProvinceId);
                if (state != null)
                    stateProvince = state.Abbreviation;
            }
            if (!String.IsNullOrEmpty(postProcessPaymentRequest.Order.ShippingAddress?.CountryId))
            {
                var country = EngineContext.Current.Resolve<ICountryService>().GetCountryById(postProcessPaymentRequest.Order.ShippingAddress?.CountryId);
                if (country != null)
                    countryCode = country.TwoLetterIsoCode;
            }

            Random random = new System.Random();
            int RandNum = random.Next(0, 1000000000);

            var status = true;
            var merchantid = _molPayPaymentSettings.MerchantId;
            var vkey = _molPayPaymentSettings.Vkey;

            var mpschannel = postProcessPaymentRequest.Order.PaymentMethodSystemName;

            //var roundedItemPrice = Math.Round(item.UnitPriceExclTax, 2);
            //roundedItemPrice.ToString("0.00", CultureInfo.InvariantCulture)

            var amount = Math.Round(postProcessPaymentRequest.Order.OrderTotal,2);
            var mpsamount = amount.ToString("0.00", CultureInfo.InvariantCulture);

            var mpscurrency = _currencyService.GetCurrencyById(_currencySettings.PrimaryStoreCurrencyId)?.CurrencyCode;
            var billFirstName = postProcessPaymentRequest.Order.BillingAddress?.FirstName;
            var billLastName = postProcessPaymentRequest.Order.BillingAddress?.LastName;
            var mpsbill_name = billFirstName + " " + billLastName;
            var mpsbill_mobile = postProcessPaymentRequest.Order.BillingAddress?.PhoneNumber;
            var FirstUrl = GetMOLPayUrl();
            var mpsorderid = postProcessPaymentRequest.Order.OrderNumber;
            var SecondUrl = FirstUrl + merchantid;
            var mpsvcode = md5encode(mpsamount + merchantid + mpsorderid + vkey);
            var address1 = postProcessPaymentRequest.Order.BillingAddress?.Address1;
            var address2 = postProcessPaymentRequest.Order.BillingAddress?.Address2;
            var mpsbill_desc = address1 + " " + address2;
            var mpsbill_email = postProcessPaymentRequest.Order.BillingAddress?.Email;
            var invoice = postProcessPaymentRequest.Order.OrderNumber.ToString();
            var custom = postProcessPaymentRequest.Order.OrderGuid.ToString();
            var ChannelType = _molPayPaymentSettings.ChannelType;
            var address_override = postProcessPaymentRequest.Order.ShippingStatus == ShippingStatus.ShippingNotRequired ? "0" : "1";
            var city = postProcessPaymentRequest.Order.ShippingAddress?.City;
            var mpscountry = countryCode;
            //var mpsapiversion = "3.16";
            var mpslangcode = "en";
            //var mpstimerbox = "#counter";

            if (merchantid == null)
            {
                throw new ArgumentNullException(nameof(merchantid));
            }

            if (vkey == null)
            {
                throw new ArgumentNullException(nameof(vkey));
            }

            //create query parameters
            return new Dictionary<string, string>
            {
                ["status"] = status.ToString(),
                ["merchant_id"] = merchantid,
                ["amount"] = mpsamount.ToString(),
                ["orderid"] = mpsorderid.ToString(),
                ["bill_name"] = mpsbill_name,
                ["bill_email"] = mpsbill_email,
                ["bill_mobile"] = mpsbill_mobile,
                ["bill_desc"] = mpsbill_desc,
                ["country"] = countryCode,
                ["vcode"] = mpsvcode,
                ["currency"] = mpscurrency,
                ["channel"] = mpschannel,
                ["langcode"] = mpslangcode,
                ["returnurl"] = $"{storeLocation}Plugins/MOLPay/PDTHandler",
                ["callbackurl"] = $"{storeLocation}/Plugins/MOLPay/IPNHandler",
                //["cancelurl"] = $"{storeLocation}Plugins/MOLPay/CancelOrder",

            };
        }

        /// <summary>
        /// Add order items to the request query parameters
        /// </summary>
        /// <param name="parameters">Query parameters</param>
        /// <param name="postProcessPaymentRequest">Payment info required for an order processing</param>
        private void AddItemsParameters(IDictionary<string, string> parameters, PostProcessPaymentRequest postProcessPaymentRequest)
        {
            //upload order items
            parameters.Add("cmd", "_cart");
            parameters.Add("upload", "1");

            var cartTotal = decimal.Zero;
            var roundedCartTotal = decimal.Zero;
            var itemCount = 1;

            //add shopping cart items
            foreach (var item in postProcessPaymentRequest.Order.OrderItems)
            {
                var product = _productService.GetProductById(item.ProductId);

                var roundedItemPrice = Math.Round(item.UnitPriceExclTax, 2);

                //add query parameters
                parameters.Add($"item_name_{itemCount}", product.Name);
                //parameters.Add($"amount_{itemCount}", roundedItemPrice.ToString("0.00", CultureInfo.InvariantCulture));
                //parameters.Add($"quantity_{itemCount}", item.Quantity.ToString());

                cartTotal += item.PriceExclTax;
                roundedCartTotal += roundedItemPrice * item.Quantity;
                itemCount++;
            }

            //add checkout attributes as order items
            var checkoutAttributeValues = _checkoutAttributeParser.ParseCheckoutAttributeValues(postProcessPaymentRequest.Order.CheckoutAttributesXml);
            var customer = EngineContext.Current.Resolve<ICustomerService>().GetCustomerById(postProcessPaymentRequest.Order.CustomerId);
            foreach (var attributeValue in checkoutAttributeValues)
            {
                var attributePrice = _taxService.GetCheckoutAttributePrice(attributeValue, false, customer);
                if (attributePrice > 0)
                {
                    var roundedAttributePrice = Math.Round(attributePrice, 2);

                    //add query parameters
                    var attribute = EngineContext.Current.Resolve<ICheckoutAttributeService>().GetCheckoutAttributeById(attributeValue.CheckoutAttributeId);
                    if (attribute != null)
                    {
                        parameters.Add($"item_name_{itemCount}", attribute.Name);
                        //parameters.Add($"amount_{itemCount}", roundedAttributePrice.ToString("0.00", CultureInfo.InvariantCulture));
                        //parameters.Add($"quantity_{itemCount}", "1");

                        cartTotal += attributePrice;
                        roundedCartTotal += roundedAttributePrice;
                        itemCount++;
                    }
                }
            }

            //add shipping fee as a separate order item, if it has price
            var roundedShippingPrice = Math.Round(postProcessPaymentRequest.Order.OrderShippingExclTax, 2);
            if (roundedShippingPrice > decimal.Zero)
            {
                parameters.Add($"item_name_{itemCount}", "Shipping fee");
                //parameters.Add($"amount_{itemCount}", roundedShippingPrice.ToString("0.00", CultureInfo.InvariantCulture));
                //parameters.Add($"quantity_{itemCount}", "1");

                cartTotal += postProcessPaymentRequest.Order.OrderShippingExclTax;
                roundedCartTotal += roundedShippingPrice;
                itemCount++;
            }

            //add payment method additional fee as a separate order item, if it has price
            var roundedPaymentMethodPrice = Math.Round(postProcessPaymentRequest.Order.PaymentMethodAdditionalFeeExclTax, 2);
            if (roundedPaymentMethodPrice > decimal.Zero)
            {
                parameters.Add($"item_name_{itemCount}", "Payment method fee");
                //parameters.Add($"amount_{itemCount}", roundedPaymentMethodPrice.ToString("0.00", CultureInfo.InvariantCulture));
                //parameters.Add($"quantity_{itemCount}", "1");

                cartTotal += postProcessPaymentRequest.Order.PaymentMethodAdditionalFeeExclTax;
                roundedCartTotal += roundedPaymentMethodPrice;
                itemCount++;
            }

            //add tax as a separate order item, if it has positive amount
            var roundedTaxAmount = Math.Round(postProcessPaymentRequest.Order.OrderTax, 2);
            if (roundedTaxAmount > decimal.Zero)
            {
                parameters.Add($"item_name_{itemCount}", "Tax amount");
                //parameters.Add($"amount_{itemCount}", roundedTaxAmount.ToString("0.00", CultureInfo.InvariantCulture));
                //parameters.Add($"quantity_{itemCount}", "1");

                cartTotal += postProcessPaymentRequest.Order.OrderTax;
                roundedCartTotal += roundedTaxAmount;
                itemCount++;
            }

            if (cartTotal > postProcessPaymentRequest.Order.OrderTotal)
            {
                //get the difference between what the order total is and what it should be and use that as the "discount"
                var discountTotal = Math.Round(cartTotal - postProcessPaymentRequest.Order.OrderTotal, 2);
                roundedCartTotal -= discountTotal;

                //gift card or rewarded point amount applied to cart in nopCommerce - shows in MOLPay as "discount"
                parameters.Add("discount_amount_cart", discountTotal.ToString("0.00", CultureInfo.InvariantCulture));
            }

            //save order total that actually sent to MOLPay (used for PDT order total validation)
            _genericAttributeService.SaveAttribute(postProcessPaymentRequest.Order, MOLPayHelper.OrderTotalSentToMOLPay, roundedCartTotal);
        }

        /// <summary>
        /// Add order total to the request query parameters
        /// </summary>
        /// <param name="parameters">Query parameters</param>
        /// <param name="postProcessPaymentRequest">Payment info required for an order processing</param>
        private void AddOrderTotalParameters(IDictionary<string, string> parameters, PostProcessPaymentRequest postProcessPaymentRequest)
        {
            //round order total
            var roundedOrderTotal = Math.Round(postProcessPaymentRequest.Order.OrderTotal, 2);

            parameters.Add("cmd", "_xclick");
            //parameters.Add("item_name", $"Order Number {postProcessPaymentRequest.Order.OrderNumber.ToString()}");
            //parameters.Add("amount", roundedOrderTotal.ToString("0.00", CultureInfo.InvariantCulture));

            //save order total that actually sent to PayPal (used for PDT order total validation)
            _genericAttributeService.SaveAttribute(postProcessPaymentRequest.Order, MOLPayHelper.OrderTotalSentToMOLPay, roundedOrderTotal);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Process a payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>Process payment result</returns>
        public ProcessPaymentResult ProcessPayment(ProcessPaymentRequest processPaymentRequest)
        {
            return new ProcessPaymentResult();
        }

        private string md5encode(string input)
        {
            using (MD5 hasher = MD5.Create())
            {

                byte[] hash = hasher.ComputeHash(Encoding.UTF8.GetBytes(input));
                StringBuilder str = new StringBuilder();
                for (int n = 0; n <= hash.Length - 1; n++)
                {
                    str.Append(hash[n].ToString("X2"));
                }

                return str.ToString().ToLower();
            }
        }

        /// <summary>
        /// Post process payment (used by payment gateways that require redirecting to a third-party URL)
        /// </summary>
        /// <param name="postProcessPaymentRequest">Payment info required for an order processing</param>
        public void PostProcessPayment(PostProcessPaymentRequest postProcessPaymentRequest)
        {
            //create common query parameters for the request
            var queryParameters = CreateQueryParameters(postProcessPaymentRequest);

            var merchantid = _molPayPaymentSettings.MerchantId;
            var vkey = _molPayPaymentSettings.Vkey;

            var FirstUrl = GetMOLPayUrl();
            var NewUrl = FirstUrl + merchantid;

            //whether to include order items in a transaction
            if (_molPayPaymentSettings.PassProductNamesAndTotals)
            {
                //add order items query parameters to the request
                var parameters = new Dictionary<string, string>(queryParameters);
                AddItemsParameters(parameters, postProcessPaymentRequest);

                //remove null values from parameters
                parameters = parameters.Where(parameter => !string.IsNullOrEmpty(parameter.Value))
                    .ToDictionary(parameter => parameter.Key, parameter => parameter.Value);

                //ensure redirect URL doesn't exceed 2K chars to avoid "too long URL" exception
                var redirectUrl = QueryHelpers.AddQueryString(NewUrl, parameters);
                if (redirectUrl.Length <= 2048)
                {
                    _httpContextAccessor.HttpContext.Response.Redirect(NewUrl);
                    return;
                }
            }

            //or add only an order total query parameters to the request
            AddOrderTotalParameters(queryParameters, postProcessPaymentRequest);

            //remove null values from parameters
            queryParameters = queryParameters.Where(parameter => !string.IsNullOrEmpty(parameter.Value))
                .ToDictionary(parameter => parameter.Key, parameter => parameter.Value);

            var url = QueryHelpers.AddQueryString(NewUrl, queryParameters);
            _httpContextAccessor.HttpContext.Response.Redirect(url);
        }

        /// <summary>
        /// Returns a value indicating whether payment method should be hidden during checkout
        /// </summary>
        /// <param name="cart">Shopping cart</param>
        /// <returns>true - hide; false - display.</returns>
        public bool HidePaymentMethod(IList<ShoppingCartItem> cart)
        {
            //you can put any logic here
            //for example, hide this payment method if all products in the cart are downloadable
            //or hide this payment method if current customer is from certain country
            return false;
        }

        /// <summary>
        /// Gets additional handling fee
        /// </summary>
        /// <param name="cart">Shopping cart</param>
        /// <returns>Additional handling fee</returns>
        public decimal GetAdditionalHandlingFee(IList<ShoppingCartItem> cart)
        {
            return this.CalculateAdditionalFee(_orderTotalCalculationService, cart,
                _molPayPaymentSettings.AdditionalFee, _molPayPaymentSettings.AdditionalFeePercentage);
        }

        /// <summary>
        /// Captures payment
        /// </summary>
        /// <param name="capturePaymentRequest">Capture payment request</param>
        /// <returns>Capture payment result</returns>
        public CapturePaymentResult Capture(CapturePaymentRequest capturePaymentRequest)
        {
            return new CapturePaymentResult { Errors = new[] { "Capture method not supported" } };
        }

        /// <summary>
        /// Refunds a payment
        /// </summary>
        /// <param name="refundPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public RefundPaymentResult Refund(RefundPaymentRequest refundPaymentRequest)
        {
            return new RefundPaymentResult { Errors = new[] { "Refund method not supported" } };
        }

        /// <summary>
        /// Voids a payment
        /// </summary>
        /// <param name="voidPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public VoidPaymentResult Void(VoidPaymentRequest voidPaymentRequest)
        {
            return new VoidPaymentResult { Errors = new[] { "Void method not supported" } };
        }

        /// <summary>
        /// Process recurring payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>Process payment result</returns>
        public ProcessPaymentResult ProcessRecurringPayment(ProcessPaymentRequest processPaymentRequest)
        {
            return new ProcessPaymentResult { Errors = new[] { "Recurring payment not supported" } };
        }

        /// <summary>
        /// Cancels a recurring payment
        /// </summary>
        /// <param name="cancelPaymentRequest">Request</param>
        /// <returns>Result</returns>
        public CancelRecurringPaymentResult CancelRecurringPayment(CancelRecurringPaymentRequest cancelPaymentRequest)
        {
            return new CancelRecurringPaymentResult { Errors = new[] { "Recurring payment not supported" } };
        }

        /// <summary>
        /// Gets a value indicating whether customers can complete a payment after order is placed but not completed (for redirection payment methods)
        /// </summary>
        /// <param name="order">Order</param>
        /// <returns>Result</returns>
        public bool CanRePostProcessPayment(Order order)
        {
            if (order == null)
                throw new ArgumentNullException(nameof(order));

            //let's ensure that at least 5 seconds passed after order is placed
            //P.S. there's no any particular reason for that. we just do it
            if ((DateTime.UtcNow - order.CreatedOnUtc).TotalSeconds < 5)
                return false;

            return true;
        }

        /// <summary>
        /// Validate payment form
        /// </summary>
        /// <param name="form">The parsed form values</param>
        /// <returns>List of validating errors</returns>
        public IList<string> ValidatePaymentForm(IFormCollection form)
        {
            return new List<string>();
        }

        /// <summary>
        /// Get payment information
        /// </summary>
        /// <param name="form">The parsed form values</param>
        /// <returns>Payment info holder</returns>
        public ProcessPaymentRequest GetPaymentInfo(IFormCollection form)
        {
            return new ProcessPaymentRequest();
        }

        /// <summary>
        /// Gets a configuration page URL
        /// </summary>
        public override string GetConfigurationPageUrl()
        {
            return $"{_webHelper.GetStoreLocation()}Admin/MOLPay/Configure";
        }

        /// <summary>
        /// Gets a name of a view component for displaying plugin in public store ("payment info" checkout step)
        /// </summary>
        /// <returns>View component name</returns>
        public string GetPublicViewComponentName()
        {
            return "MOLPay";
        }

        /// <summary>
        /// Install the plugin
        /// </summary>
        public override void Install()
        {
            //settings
            _settingService.SaveSetting(new MOLPayPaymentSettings
            {
                UseSandbox = true,
                CapturedMode = PaymentStatus.Paid,
                PendingMode = PaymentStatus.Pending,
                FailedMode = PaymentStatus.Voided
            });

            //locales

            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.MerchantId", "MerchantId");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.MerchantId.Hint", "Specify your MOLPay Merchant Id.");

            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.Vkey", "Vkey");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.Vkey.Hint", "Specify your MOLPay Vkey.");

            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.UseSandbox", "Use Sandbox");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.UseSandbox.Hint", "Check to enable Sandbox (testing environment).");

            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.Skey", "Secret Key");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.Skey.Hint", "Specify your secret key.");


            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.CapturedMode", "Captured");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.CapturedMode.Hint", "Mapping for status Captured");

            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.PendingMode", "Pending");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.PendingMode.Hint", "Mapping for status Pending");

            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.FailedMode", "Failed");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.FailedMode.Hint", "Mapping for status Failed");

            //this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.PaymentStatusId", "Payment Status");

            this.AddOrUpdatePluginLocaleResource("Enums.Grand.Core.Domain.Payments.PaymentStatus.Pending", "Pending");
            this.AddOrUpdatePluginLocaleResource("Enums.Grand.Core.Domain.Payments.PaymentStatus.Authorized", "Authorized");
            this.AddOrUpdatePluginLocaleResource("Enums.Grand.Core.Domain.Payments.PaymentStatus.Paid", "Paid");
            this.AddOrUpdatePluginLocaleResource("Enums.Grand.Core.Domain.Payments.PaymentStatus.PartiallyRefunded", "PartiallyRefunded");
            this.AddOrUpdatePluginLocaleResource("Enums.Grand.Core.Domain.Payments.PaymentStatus.Refunded", "Refunded");
            this.AddOrUpdatePluginLocaleResource("Enums.Grand.Core.Domain.Payments.PaymentStatus.Voided", "Voided");

            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.AdditionalFee", "Additional fee");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.AdditionalFee.Hint", "Enter additional fee to charge your customers.");

            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.AdditionalFeePercentage", "Additional fee. Use percentage");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.AdditionalFeePercentage.Hint", "Determines whether to apply a percentage additional fee to the order total. If not enabled, a fixed value is used.");

            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.PassProductNamesAndTotals", "Pass product names and order totals to MOLPay");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.PassProductNamesAndTotals.Hint", "Check if product names and order totals should be passed to MOLPay.");

            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.PDTValidateOrderTotal", "PDT. Validate order total");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.PDTValidateOrderTotal.Hint", "Check if PDT handler should validate order totals.");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Fields.RedirectionTip", "You will be redirected to MOLPay site to complete the order.");

            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.Instructions", "<p><b>If you're using this gateway ensure that your primary store currency is supported by MOLPay.</b><br /><br />To use PDT, you must activate PDT and Auto Return in your MOLPay account profile. You must also acquire a PDT identity token, which is used in all PDT communication you send to MOLPay. Follow these steps to configure your account for PDT:<br /><br />1. Log in to your MOLPay account (click <a href=\"https://www.paypal.com/us/webapps/mpp/referral/paypal-business-account2?partner_id=9JJPJNNPQ7PZ8\" target=\"_blank\">here</a> to create your account).<br />2. Click the Profile subtab.<br />3. Click Website Payment Preferences in the Seller Preferences column.<br />4. Under Auto Return for Website Payments, click the On radio button.<br />5. For the Return URL, enter the URL on your site that will receive the transaction ID posted by MOLPay after a customer payment ({0}).<br />6. Under Payment Data Transfer, click the On radio button.<br />7. Click Save.<br />8. Click Website Payment Preferences in the Seller Preferences column.<br />9. Scroll down to the Payment Data Transfer section of the page to view your PDT identity token.<br /><br /></p>");
            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.PaymentMethodDescription", "You will be redirected to MOLPay site to complete the payment");

            this.AddOrUpdatePluginLocaleResource("Plugins.Payments.MOLPay.RoundingWarning", "It looks like you have \"ShoppingCartSettings.RoundPricesDuringCalculation\" setting disabled. Keep in mind that this can lead to a discrepancy of the order total amount, as MOLPay only rounds to two decimals.");

            base.Install();
        }

        /// <summary>
        /// Uninstall the plugin
        /// </summary>
        public override void Uninstall()
        {
            //settings
            _settingService.DeleteSetting<MOLPayPaymentSettings>();

            //locales
            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.MerchantId");
            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.MerchantId.Hint");

            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.Vkey");
            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.Vkey.Hint");

            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.UseSandbox");
            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.UseSandbox.Hint");

            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.Skey");
            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.Skey.Hint");

            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.CapturedMode");
            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.CapturedMode.Hint");

            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.PendingMode");
            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.PendingMode.Hint");

            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.FailedMode");
            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.FailedMode.Hint");

            this.DeletePluginLocaleResource("Enum.Grand.Core.Domain.Payments.PaymentStatus.Pending");
            this.DeletePluginLocaleResource("Enum.Grand.Core.Domain.Payments.PaymentStatus.Authorized");
            this.DeletePluginLocaleResource("Enum.Grand.Core.Domain.Payments.PaymentStatus.Paid");
            this.DeletePluginLocaleResource("Enum.Grand.Core.Domain.Payments.PaymentStatus.PartiallyRefunded");
            this.DeletePluginLocaleResource("Enum.Grand.Core.Domain.Payments.PaymentStatus.Refunded");
            this.DeletePluginLocaleResource("Enum.Grand.Core.Domain.Payments.PaymentStatus.Voided");

            //this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.PaymentStatusId");

            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.AdditionalFee");
            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.AdditionalFee.Hint");

            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.AdditionalFeePercentage");
            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.AdditionalFeePercentage.Hint");

            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.PassProductNamesAndTotals");
            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.PassProductNamesAndTotals.Hint");

            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Fields.RedirectionTip");

            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.Instructions");
            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.PaymentMethodDescription");
            this.DeletePluginLocaleResource("Plugins.Payments.MOLPay.RoundingWarning");

            base.Uninstall();
        }

        public void GetPublicViewComponent(out string viewComponentName)
        {
            viewComponentName = "MOLPay";
        }

        public Type GetControllerType()
        {
            return typeof(MOLPayController);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets a value indicating whether capture is supported
        /// </summary>
        public bool SupportCapture
        {
            get { return false; }
        }

        /// <summary>
        /// Gets a value indicating whether partial refund is supported
        /// </summary>
        public bool SupportPartiallyRefund
        {
            get { return false; }
        }

        /// <summary>
        /// Gets a value indicating whether refund is supported
        /// </summary>
        public bool SupportRefund
        {
            get { return false; }
        }

        /// <summary>
        /// Gets a value indicating whether void is supported
        /// </summary>
        public bool SupportVoid
        {
            get { return false; }
        }

        /// <summary>
        /// Gets a recurring payment type of payment method
        /// </summary>
        public RecurringPaymentType RecurringPaymentType
        {
            get { return RecurringPaymentType.NotSupported; }
        }

        /// <summary>
        /// Gets a payment method type
        /// </summary>
        public PaymentMethodType PaymentMethodType
        {
            get { return PaymentMethodType.Redirection; }
        }

        /// <summary>
        /// Gets a value indicating whether we should display a payment information page for this plugin
        /// </summary>
        public bool SkipPaymentInfo
        {
            get { return false; }
        }

        /// <summary>
        /// Gets a payment method description that will be displayed on checkout pages in the public store
        /// </summary>
        public string PaymentMethodDescription
        {
            //return description of this payment method to be display on "payment method" checkout step. good practice is to make it localizable
            //for example, for a redirection payment method, description may be like this: "You will be redirected to MOLPay site to complete the payment"
            get { return _localizationService.GetResource("Plugins.Payments.MOLPay.PaymentMethodDescription"); }

            //get { return _localizationService.GetResource("<select>" +
            //    "<option value='maybank2u'>Maybank2u</option>" +
            //    "<option value='cimbclicks'>CIMBClicks</option>" +
            //    "</select>"); }
        }

        #endregion
    }
}