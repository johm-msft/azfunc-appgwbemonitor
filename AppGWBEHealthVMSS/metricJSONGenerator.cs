using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Http;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

using Microsoft.Extensions.Logging;

namespace AppGWBEHealthVMSS
{
    static class metricJSONGenerator
    {
        static int metricCount = 1;
        private static string authToken = null;
        private static DateTimeOffset authTokenLifetime;
        private static readonly HttpClient client = new HttpClient();
        private static Random rndGen = new Random();


        ///
        public static bool populateCustomMetric(string metricName, string payload, string appGwName, string targetRegion, string targetResourceID, ILogger log, string clientID, string clientSecret, string tenantID, bool logCustomMetricsVerboseLogging)
        {
            try
            {
                 Uri targetUri = new Uri($"https://{targetRegion}.monitoring.azure.com{targetResourceID}/metrics");

                
                // Get authToken 
                bool forceCacheClear = rndGen.Next(25) == 0 | authTokenLifetime > DateTimeOffset.UtcNow.AddMinutes(-15); 
                if (metricJSONGenerator.authToken == null || forceCacheClear )
                {
                    log.LogInformation($"CustomMetric: No cached AuthToken - generating. ForceCacheClear: {forceCacheClear}");
                   
                    var authContext = new AuthenticationContext($"https://login.microsoftonline.com/{tenantID}/");

                    var credential = new ClientCredential(clientID, clientSecret);
                    var result = (AuthenticationResult)authContext
                        .AcquireTokenAsync("https://monitoring.azure.com/", credential)
                        .Result;
                    authToken = result.AccessToken;
                    authTokenLifetime = result.ExpiresOn;
                    log.LogInformation($"CustomMetric: Populated AuthToken good until {authTokenLifetime}");


                }
                else
                {
                    log.LogInformation($"CustomMetric: Using Cached AuthToken good until {authTokenLifetime}");
                }
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);

                string content = getMetricJSON(metricName, payload, appGwName, metricCount++);
                if (logCustomMetricsVerboseLogging)
                {
                    log.LogInformation($"CustomMetric: attempting to log custom metric: {content}");
                }
                else
                {
                    log.LogInformation($"CustomMetric: attempting to log custom metric: {metricName} -> {payload}");
                }
                HttpRequestMessage req = new HttpRequestMessage()
                {
                    RequestUri = targetUri,
                    Method = HttpMethod.Post,
                };
                client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                req.Content = new StringContent(content, Encoding.Default, "application/json");
                var task = client.SendAsync(req).ContinueWith((taskwithmsg) =>
                {
                    var response = taskwithmsg.Result;
                    string responseString = response.Content.ReadAsStringAsync().Result;
                    if (logCustomMetricsVerboseLogging || !response.IsSuccessStatusCode)
                    {
                        log.LogInformation($"CustomMetric: Metric write result: {response} {metricName} -> {payload}; content: {responseString}");
                    }
                    else
                    {
                        log.LogInformation($"CustomMetric: Metric write result: {response.StatusCode} for {metricName} -> {payload}");
                    }
                });
                return (true);
            }
            catch(Exception err)
            {
                log.LogInformation($"Failed to populate custom metric: {err}");
                return false;
            }
        }


        /// <summary>
        /// This is incredibly crude, but it'll work for now. Move to an object that can be parsed w/ Newtonsoft
        /// </summary>
        /// <param name="metricName"></param>
        /// <param name="payload"></param>
        /// <param name="appGWName"></param>
        /// <param name="count"></param>
        /// <returns>string containing json representation of a custom metric</returns>
        public static string getMetricJSON(string metricName, string payload, string appGWName, int count)

        {
            string templateString = @"{ 
    ""time"": ""{CURTIME}"", 
    ""data"": {
                ""baseData"": {
                    ""metric"": ""{METRICNAME}"", 
            ""namespace"": ""custommetric"", 
            ""dimNames"": [   ""AppGwName"" 
            ], 
            ""series"": [
              { 
                ""dimValues"": [ ""{APPGWNAME}""
                ], 
                ""min"": {PAYLOAD}, 
                ""max"": {PAYLOAD}, 
                ""sum"": {PAYLOAD}, 
                ""count"": 1 
              } 
            ] 
        } 
    } 
} ".Replace("{APPGWNAME}", appGWName).Replace("{CURTIME}", DateTime.UtcNow.ToString("s", System.Globalization.CultureInfo.InvariantCulture)).Replace("{METRICNAME}", metricName).Replace("{PAYLOAD}", payload).Replace("{FAKEDATE}", Convert.ToInt32(DateTime.UtcNow.ToString("hhmmss")).ToString()).Replace("{COUNT}", count.ToString());
            return (templateString);
        }

    }
}
