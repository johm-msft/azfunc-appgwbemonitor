using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.Network.Fluent;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using AppGWBEHealthVMSS.shared;



namespace AppGWBEHealthVMSS
{
    public static class AppGWBEHealth
    {
        [FunctionName("AppGWBEHealthProbe")]
        public static void Run([TimerTrigger("0 * * * * *")]TimerInfo myTimer, ILogger log)
        {
            string clientID = System.Environment.GetEnvironmentVariable("clientID");
            string clientSecret = System.Environment.GetEnvironmentVariable("clientSecret");
            string tenantID = System.Environment.GetEnvironmentVariable("tenantID");
            string location = System.Environment.GetEnvironmentVariable("location");
            string subscriptionID = System.Environment.GetEnvironmentVariable("subscriptionID");
            string resourcegroupname = System.Environment.GetEnvironmentVariable("resourceGroupName");
            string appGwName = System.Environment.GetEnvironmentVariable("appGwName");
            string scaleSetName = System.Environment.GetEnvironmentVariable("scaleSetName");

            try
            {
                log.LogInformation("Creating Azure Client");
                var azEnvironment = AzureEnvironment.AzureGlobalCloud;
                var azClient = AzureClient.CreateAzureClient(clientID, clientSecret, tenantID, azEnvironment, subscriptionID);
                log.LogInformation("Checking Application Gateway BE ");
                ApplicationGatewayOperations.CheckApplicationGatewayBEHealth(azClient, resourcegroupname, appGwName, scaleSetName, log);
            }
            catch (Exception e)
            {
                log.LogInformation("Error Message: " + e.Message);
            }







        }
    }
}
