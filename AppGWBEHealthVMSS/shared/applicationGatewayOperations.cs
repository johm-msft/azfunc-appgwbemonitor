using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.Network.Fluent;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;

namespace AppGWBEHealthVMSS.shared
{
    class ApplicationGatewayOperations
    {
        public static void CheckApplicationGatewayBEHealth(IAzure azureClient, string rgName, string appGwName,string scaleSetName, ILogger log)
        {
            try
            {
                log.LogInformation("Enumerating Application Gateway: {0} Backend Unhealthy Servers", appGwName);
                var appGw = azureClient.ApplicationGateways.Inner.BackendHealthAsync(rgName, appGwName).Result;
                var servers = appGw.BackendAddressPools[0].BackendHttpSettingsCollection[0].Servers.Where(x => x.Health.Value.Equals("Unhealthy"));
                                
                foreach (var server in servers)
                {
                    
                   log.LogInformation("Server: {0} is unhealthy, triggering removal from scaleset: {1}", server.Address, scaleSetName);
                   VmScaleSetOperations.RemoveVMSSInstanceByID(azureClient, rgName, scaleSetName, server.Address, log);
                                   

                }
            }
            catch (Exception e)
            {
                log.LogInformation("Error Message: " + e.Message);
            }

        }

    }
}
