using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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
                List<string> appGwBadIps = new List<string>();

                foreach (var server in servers)
                {
                    var healthyServers = appGw.BackendAddressPools[0].BackendHttpSettingsCollection[0].Servers.Where(x => x.Health.Value.Equals("Healthy"));
                    log.LogInformation("Healthy Server Count: {0}", healthyServers.Count().ToString());
                    if (healthyServers.Count() <3)
                    {
                        var scaleSet = azureClient.VirtualMachineScaleSets.GetByResourceGroup(rgName, scaleSetName);
                        if(scaleSet.Inner.ProvisioningState == "Succeeded")
                        {
                            log.LogInformation("No updates occuring on ScaleSet");
                            log.LogInformation("Trigger Scale Event as Healthy Nodes is less than 3");
                            int scaleNodeCount = healthyServers.Count() + 3;
                            VmScaleSetOperations.ScaleEvent(azureClient, rgName, scaleSetName, scaleNodeCount, log);
                        }
                    }
                    appGwBadIps.Add(server.Address);                   
                                   

                }

                log.LogInformation("Unhealthy nodes being removed");
                VmScaleSetOperations.RemoveVMSSInstanceByID(azureClient, rgName, scaleSetName, appGwBadIps, log);
            }
            catch (Exception e)
            {
                log.LogInformation("Error Message: " + e.Message);
                log.LogInformation("HResult: " + e.HResult);
                log.LogInformation("InnerException:" + e.InnerException);

            }

        }
        public static int GetConcurrentConnectionCountAppGW(IAzure azureClient, string rgName, string appGwName, ILogger log)
        {
            try
            {
                int avgConcurrentConnections = 0;
                var appGW = azureClient.ApplicationGateways.GetByResourceGroup(rgName, appGwName);
                log.LogInformation("Getting Metric Definitions");
                var metricDefs = azureClient.MetricDefinitions.ListByResource(appGW.Id).Where(x => x.Name.LocalizedValue == "Current Connections");
                DateTime recordDateTime = DateTime.Now.ToUniversalTime();
                
                foreach (var metricDef in metricDefs)
                {
                    log.LogInformation("Running Metric Query");
                    var metricCollection = metricDef.DefineQuery().StartingFrom(recordDateTime.AddMinutes(-1)).EndsBefore(recordDateTime).WithAggregation("Average").Execute();
                         
                      foreach (var metric in metricCollection.Metrics)
                      {
                             
                         foreach (var timeElement in metric.Timeseries)
                         {
                                                                   
                                    
                            foreach (var data in timeElement.Data)
                            {

                                    log.LogInformation("Avgerage Concurrent Connections: {0}", data.Average);
                                    avgConcurrentConnections = Convert.ToInt32(data.Average);
                                    

                            }

                         }
                      }

                    

                }
                return avgConcurrentConnections;    
            }
            catch (Exception e)
            {
                
                log.LogInformation("Error Message: " + e.Message);
                log.LogInformation("HResult: " + e.HResult);
                log.LogInformation("InnerException:" + e.InnerException);
                return 0;
            }
            
        }
        public static int AvgConnectionsPerNode(IAzure azureClient, string rgName, string appGwName, int conCurrentConnections, ILogger log)
        {
            try
            {
                var appGw = azureClient.ApplicationGateways.Inner.BackendHealthAsync(rgName, appGwName).Result;
                var servers = appGw.BackendAddressPools[0].BackendHttpSettingsCollection[0].Servers.Where(x => x.Health.Value.Equals("Healthy"));
                var healthyServersCount = servers.Count();
                int avgConnsPerNode = conCurrentConnections / healthyServersCount;
                log.LogInformation("Healthy Node Count: {0} ",healthyServersCount);
                log.LogInformation("Average Connections Per node: {0}", avgConnsPerNode);
                return avgConnsPerNode;

            }
            catch (Exception e)
            {
                log.LogInformation("Error Message: " + e.Message);
                return 0;
            }
        }

    }
}
