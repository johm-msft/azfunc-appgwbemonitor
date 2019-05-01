using System;
using System.Linq;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using AppGWBEHealthVMSS.shared;
using Microsoft.Azure.Management.ResourceManager.Fluent;

namespace AppGWBEHealthVMSS
{
    public static class CheckConcurrentCons
    {
        [FunctionName("checkConcurrentCons")]
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
                log.LogInformation("Creating Azure Client for checkConcurrentCons Function");
                var azEnvironment = AzureEnvironment.AzureGlobalCloud;
                var azClient = AzureClient.CreateAzureClient(clientID, clientSecret, tenantID, azEnvironment, subscriptionID);
                log.LogInformation("Getting Current Connection Count");
                var currentConnectionCount = ApplicationGatewayOperations.GetConcurrentConnectionCountAppGW(azClient, resourcegroupname, appGwName, log);
                log.LogInformation("Calculating Average Connections Per Node");
                var avgConnectionsPerNode = ApplicationGatewayOperations.AvgConnectionsPerNode(azClient, resourcegroupname, appGwName, currentConnectionCount, log);
                log.LogInformation("Calculating Ideal Node Count");
                var idealNumberofNodes = ApplicationGatewayOperations.IdealNumberofNodes(azClient, resourcegroupname, appGwName, currentConnectionCount, log);

                if(avgConnectionsPerNode >= 3)
                {
                   if(idealNumberofNodes <= 10)
                   {
                       int scaleNodeCount = idealNumberofNodes;
                       log.LogInformation("Scale Event Initiated");
                       VmScaleSetOperations.ScaleEvent(azClient, resourcegroupname, scaleSetName, scaleNodeCount, log);

                   } 
                   else 
                   {
                        int scaleNodeCount = 10;
                        log.LogInformation("Scale Event Initiated");
                        VmScaleSetOperations.ScaleEvent(azClient, resourcegroupname, scaleSetName, scaleNodeCount, log);
                   }
                   

                }
                


            }
            catch (Exception e)
            {
                log.LogInformation("Error Message: " + e.Message);
            }
            
        }
    }
}
