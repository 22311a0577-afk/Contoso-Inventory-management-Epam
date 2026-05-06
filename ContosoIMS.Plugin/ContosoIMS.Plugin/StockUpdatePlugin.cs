using System;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;

namespace ContosoIMS.Plugin
{
    public class StockUpdatePlugin : IPlugin
    {
        private string _functionUrl;
        private string _functionKey;

        public StockUpdatePlugin(string unsecureConfig, string secureConfig)
        {
            if (string.IsNullOrWhiteSpace(unsecureConfig))
                throw new InvalidPluginExecutionException("Plugin configuration is missing.");

            try
            {
                var config = JsonConvert.DeserializeObject<PluginConfig>(unsecureConfig);

                if (config == null || string.IsNullOrEmpty(config.FunctionUrl))
                    throw new InvalidPluginExecutionException("FunctionUrl missing in config.");

                _functionUrl = config.FunctionUrl;
                _functionKey = config.FunctionKey ?? "";
            }
            catch (InvalidPluginExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidPluginExecutionException("Invalid plugin config: " + ex.Message);
            }
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            ITracingService tracingService =
                (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            IPluginExecutionContext context =
                (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

            // --- Guard clauses ---
            if (context.MessageName != "Create")
            {
                tracingService.Trace("Skipping — not a Create message.");
                return;
            }

            if (context.Stage != 40)
            {
                tracingService.Trace("Skipping — not PostOperation stage.");
                return;
            }

            if (!context.InputParameters.Contains("Target"))
            {
                tracingService.Trace("Skipping — no Target in InputParameters.");
                return;
            }

            Entity target = context.InputParameters["Target"] as Entity;
            if (target == null)
            {
                tracingService.Trace("Skipping — Target is null.");
                return;
            }

            if (!context.OutputParameters.Contains("id"))
            {
                tracingService.Trace("Skipping — no id in OutputParameters.");
                return;
            }

            // ✅ FIXED — Guid direct cast, not EntityReference
            Guid recordId = (Guid)context.OutputParameters["id"];
            tracingService.Trace("Record ID: " + recordId);

            IOrganizationServiceFactory serviceFactory =
                (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            IOrganizationService service =
                serviceFactory.CreateOrganizationService(context.UserId);

            tracingService.Trace("StockUpdatePlugin started.");

            // -------------------------------------------------------
            // STEP 1: Read values from the incoming record
            // -------------------------------------------------------
            string sku = GetString(target, "cim_sku");
            int quantity = GetInteger(target, "cim_quantity");
            string source = GetString(target, "cim_source");
            string notes = GetString(target, "cim_notes");

            int transactionTypeValue = GetOptionSetValue(target, "cim_transactiontype");
            string transactionType = transactionTypeValue == 767270000 ? "Inbound" : "Outbound";

            tracingService.Trace($"Read values — SKU: {sku}, Type: {transactionType}, Qty: {quantity}, Source: {source}");

            // -------------------------------------------------------
            // STEP 2: Validate
            // -------------------------------------------------------
            if (string.IsNullOrWhiteSpace(sku))
            {
                tracingService.Trace("Validation failed — SKU is empty.");
                UpdateRecord(service, recordId, 767270003, 0, false, "", "Validation failed: SKU is required.", tracingService);
                return;
            }

            if (quantity <= 0)
            {
                tracingService.Trace("Validation failed — Quantity is zero or negative.");
                UpdateRecord(service, recordId, 767270003, 0, false, "", "Validation failed: Quantity must be greater than zero.", tracingService);
                return;
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                tracingService.Trace("Validation failed — Source is empty.");
                UpdateRecord(service, recordId, 767270003, 0, false, "", "Validation failed: Source is required.", tracingService);
                return;
            }

            // -------------------------------------------------------
            // STEP 3: Build payload
            // -------------------------------------------------------
            var payload = new
            {
                sku = sku,
                transactionType = transactionType,
                quantity = quantity,
                source = source,
                notes = notes
            };

            string jsonPayload = JsonConvert.SerializeObject(payload);
            tracingService.Trace("Payload: " + jsonPayload);

            // -------------------------------------------------------
            // STEP 4: Call Azure Function
            // -------------------------------------------------------
            try
            {
                string url = string.IsNullOrEmpty(_functionKey)
                    ? _functionUrl
                    : _functionUrl + "?code=" + _functionKey;

                tracingService.Trace("Calling Azure Function: " + _functionUrl);

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 25000;

                byte[] data = Encoding.UTF8.GetBytes(jsonPayload);
                request.ContentLength = data.Length;

                using (Stream stream = request.GetRequestStream())
                    stream.Write(data, 0, data.Length);

                // -------------------------------------------------------
                // STEP 5: Handle response
                // -------------------------------------------------------
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string responseBody = reader.ReadToEnd();
                    tracingService.Trace("Azure Function response: " + responseBody);

                    AzureFunctionResponse result =
                        JsonConvert.DeserializeObject<AzureFunctionResponse>(responseBody);

                    if (result == null)
                        throw new Exception("Empty or unreadable response from Azure Function.");

                    // -------------------------------------------------------
                    // STEP 6: Update record via OrgService
                    // -------------------------------------------------------
                    UpdateRecord(
                        service,
                        recordId,
                        result.Success ? 767270002 : 767270003,
                        result.Success ? result.NewStockLevel : 0,
                        result.AlertTriggered,
                        result.TransactionId,
                        result.Message,
                        tracingService
                    );

                    tracingService.Trace($"Plugin completed — Success: {result.Success}, NewStock: {result.NewStockLevel}");
                }
            }
            catch (WebException webEx)
            {
                string error = webEx.Message;

                if (webEx.Response != null)
                {
                    using (StreamReader reader = new StreamReader(webEx.Response.GetResponseStream()))
                    {
                        string rawError = reader.ReadToEnd();
                        tracingService.Trace("WebException raw response: " + rawError);

                        try
                        {
                            AzureFunctionResponse errResult =
                                JsonConvert.DeserializeObject<AzureFunctionResponse>(rawError);
                            error = errResult?.Message ?? rawError;
                        }
                        catch
                        {
                            error = rawError;
                        }
                    }
                }

                tracingService.Trace("WebException: " + error);
                UpdateRecord(service, recordId, 767270003, 0, false, "", "Function error: " + error, tracingService);
            }
            catch (Exception ex)
            {
                tracingService.Trace("Exception: " + ex.Message);
                UpdateRecord(service, recordId, 767270003, 0, false, "", "Plugin error: " + ex.Message, tracingService);
            }
        }

        // -------------------------------------------------------
        // Update record via OrgService (PostOperation)
        // -------------------------------------------------------
        private static void UpdateRecord(
            IOrganizationService service,
            Guid recordId,
            int processingStatus,
            int newStockLevel,
            bool alertTriggered,
            string transactionId,
            string responseMessage,
            ITracingService tracingService)
        {
            try
            {
                Entity update = new Entity("cim_stockupdaterequest", recordId);
                update["cim_status"] = new OptionSetValue(processingStatus);
                update["cim_newstocklevel"] = newStockLevel;
                update["cim_alerttriggered"] = alertTriggered;
                update["cim_transactionid"] = Truncate(transactionId, 100);
                update["cim_responsemessage"] = Truncate(responseMessage, 500);

                service.Update(update);
                tracingService.Trace("Record updated successfully.");
            }
            catch (Exception ex)
            {
                tracingService.Trace("UpdateRecord failed: " + ex.Message);
                throw;
            }
        }

        // -------------------------------------------------------
        // Helpers
        // -------------------------------------------------------

        private static string GetString(Entity e, string attr)
        {
            if (!e.Contains(attr) || e[attr] == null)
                return "";
            return e[attr].ToString().Trim();
        }

        private static int GetInteger(Entity e, string attr)
        {
            if (!e.Contains(attr) || e[attr] == null)
                return 0;
            return Convert.ToInt32(e[attr]);
        }

        private static int GetOptionSetValue(Entity e, string attr)
        {
            if (!e.Contains(attr) || e[attr] == null)
                return 0;
            return ((OptionSetValue)e[attr]).Value;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }

    // -------------------------------------------------------
    // Config & Response Models
    // -------------------------------------------------------

    public class PluginConfig
    {
        public string FunctionUrl { get; set; }
        public string FunctionKey { get; set; }
    }

    public class AzureFunctionResponse
    {
        public bool Success { get; set; }
        public int NewStockLevel { get; set; }
        public bool AlertTriggered { get; set; }
        public string TransactionId { get; set; }
        public string Message { get; set; }
    }
}