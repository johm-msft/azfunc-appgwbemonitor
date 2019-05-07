using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Management.Network.Fluent;
using Microsoft.Azure.Management.Network.Fluent.Models;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.Compute.Fluent;

namespace AppGWBEHealthVMSS.shared
{
    /// <summary>
    /// Helper class for performing operations on AppGateway
    /// </summary>
    class ApplicationGatewayOperations
    {
        /// <summary>
        /// Checks the application gateway back end health metrics and deletes Gobibear Intentional Panic nodes
        /// </summary>
        /// <returns><c>true</c>, if we deleted nodes, <c>false</c> otherwise.</returns>
        /// <param name="appGw">App gateway</param>
        /// <param name="scaleSet">Scale set.</param>
        /// <param name="minHealthyServers">Minimum healthy servers.</param>
        /// <param name="log">Log.</param>
        public static bool CheckApplicationGatewayBEHealthAndDeleteBadNodes(ApplicationGatewayBackendHealthInner appGw, IVirtualMachineScaleSet scaleSet, int minHealthyServers, ILogger log)
        {
            try
            {
                log.LogInformation("Enumerating Application Gateway Backend Servers");
                var healthy = new List<ApplicationGatewayBackendHealthServer>();
                var unhealthy = new List<ApplicationGatewayBackendHealthServer>();
                foreach (var server in appGw.BackendAddressPools[0].BackendHttpSettingsCollection[0].Servers)
                {
                    if (server.Health.Value == "Healthy")
                    {
                        healthy.Add(server);
                    }
                    else
                    {
                        unhealthy.Add(server);
                    }
                }
     
                List<string> appGwBadIps = new List<string>();

                // If we have unhealthy nodes, then delete them
                if (unhealthy.Count > 0)
                {
                    log.LogInformation("Gobibear Intentional Panic Instance node count = {0}, removing nodes", unhealthy.Count);
                    return VmScaleSetOperations.RemoveVMSSInstancesByIP(scaleSet, unhealthy.Select(s => s.Address).ToList(), log);
                }
                return false;
            }
            catch (Exception e)
            {
                log.LogInformation("Error Message: " + e.Message);
                log.LogInformation("HResult: " + e.HResult);
                log.LogInformation("InnerException:" + e.InnerException);
                return false;
            }
        }

        /// <summary>
        /// Gets current load metrics based on the scenario description. Used for simulation during development
        /// </summary>
        /// <returns>The fake concurrent connection count app gw.</returns>
        /// <param name="appGW">App gw.</param>
        /// <param name="azureClient">Azure client.</param>
        /// <param name="secondsIn">Seconds in.</param>
        /// <param name="log">Log.</param>
        public static ConnectionInfo GetFakeConnectionMetrics(IApplicationGateway appGW, IAzure azureClient, int secondsIn, ILogger log)
        {
            ConnectionInfo ret = new ConnectionInfo();
            // try to mimic the load profile we are trying to get to.

            // first section does 5 rps for 90 seconds (0-90)
            // gap of 60 seconds with no load (90-150)
            // 60 second rampup to 150 rps (150-210)
            // 300 seconds at 150 rps (210-510)
            // gap of 60 seconds with no load (510-570)
            // 75 second rampup to 450 rps (570-645)
            // 285 seconds at 450 rps (645-930)
            // gap of 60 seconds with no load (930-990)
            // 360 seconds at 60 (990-1350)

            if (secondsIn <= 90)
            {
                log.LogInformation($"Fake Load Phase 1 {secondsIn} of 90");
                ret.ResponseStatus = 5 * 60;
            }
            else if(secondsIn > 90 && secondsIn <= 150)
            {
                // gap
                log.LogInformation($"Gap after Phase 1 {secondsIn} between 90 and 150");
                ret.ResponseStatus = 0;
            }
            else if (secondsIn >150 && secondsIn <= 210)
            {
                log.LogInformation($"Phase 2 ramp {secondsIn} between 150 and 210");
                // ramp to 150 
                ret.ResponseStatus = Convert.ToInt32((((secondsIn - 150) / 60.0)) * 150) * 60;
            }
            else if (secondsIn > 210 && secondsIn <= 510)
            {
                log.LogInformation($"Phase 2 steady state {secondsIn} between 150 and 210");
                // 150 rps
                ret.ResponseStatus = 150 * 60;
            }
            else if (secondsIn > 510 && secondsIn <= 570)
            {
                log.LogInformation("Gap after Phase 2 {secondsIn} between 150 and 210");
                // gap
                ret.ResponseStatus = 0;
            }
            else if (secondsIn > 570 && secondsIn <= 645)
            {
                log.LogInformation($"Phase 3 ramp {secondsIn} between 570 and 645");
                // ramp to 450 
                ret.ResponseStatus = Convert.ToInt32((((secondsIn - 570) / 75.0)) * 450) * 60;
            }
            else if (secondsIn > 645 && secondsIn <= 930)
            {
                log.LogInformation($"Phase 3 steady state {secondsIn} between 645 and 930");
                // 285 at 450 rps
                ret.ResponseStatus = 450 * 60;
            }
            else if (secondsIn > 930 && secondsIn <= 990)
            {
                log.LogInformation($"Cool down {secondsIn} between 930 and 990");
                // gap
                ret.ResponseStatus = 0;
            }
            else if (secondsIn > 990)
            {
                // gap
                ret.ResponseStatus = 0;
            }

            return ret;
        }

        /// <summary>
        /// Gets the concurrent connection count app gw.
        /// </summary>
        /// <returns>The concurrent connection count app gw.</returns>
        /// <param name="appGW">App gw.</param>
        /// <param name="azureClient">Azure client.</param>
        /// <param name="log">Log.</param>
        public static ConnectionInfo GetConnectionMetrics(IApplicationGateway appGW, IAzure azureClient, ILogger log)
        {
            try
            {
                Dictionary<string, List<object>> metricsByName = new Dictionary<string, List<object>>();
                ConnectionInfo ret = new ConnectionInfo();


                log.LogInformation("Getting Metric Definitions");
                var metricDefs = azureClient.MetricDefinitions.ListByResource(appGW.Id);
                DateTime recordDateTime = DateTime.Now.ToUniversalTime().AddMinutes(1);

                foreach (var metricDef in metricDefs)
                {
                    // Go back, 5 mins and grab most recent value
                    var metricCollection = metricDef.DefineQuery().StartingFrom(recordDateTime.AddMinutes(-6)).EndsBefore(recordDateTime).WithAggregation("Total").Execute();
                    foreach (var metric in metricCollection.Metrics)
                    {
                        foreach (var timeElement in metric.Timeseries)
                        {
                            foreach (var data in timeElement.Data)
                            {
                                if (metric.Name.Inner.LocalizedValue == "Current Connections")
                                {
                                    ret.HistoricalConcurrentConnections.Add(data.Total);
                                    if (data.Total.HasValue)
                                    {
                                        ret.CurrentConnections = Convert.ToInt32(data.Total);
                                    }
                                }
                                if (metric.Name.Inner.LocalizedValue == "Total Requests")
                                {
                                    ret.HistoricalTotalRequests.Add(data.Total);
                                    if (data.Total.HasValue)
                                    {
                                        ret.TotalRequests = Convert.ToInt32(data.Total);
                                    }
                                }
                                if (metric.Name.Inner.LocalizedValue == "Response Status")
                                {
                                    ret.HistoricalResponseStatus.Add(data.Total);
                                    if (data.Total.HasValue)
                                    {
                                        ret.ResponseStatus = Convert.ToInt32(data.Total);
                                    }
                                }
                                if (metricsByName.ContainsKey(metric.Name.Inner.Value))
                                {
                                    metricsByName[metric.Name.Inner.Value].Add(data.Total);
                                }
                                else
                                {
                                    metricsByName[metric.Name.Inner.Value] = new List<object>() { data.Total };
                                }
                            }
                        }
                    }
                }

                //DEBUG LOGGING
                /*Console.WriteLine("Metrics:");
                foreach (var x in metricsByName.Keys)
                {
                    Console.WriteLine($"{x} : {string.Join(",",metricsByName[x])}");
                }
                */
                return ret;
            }
            catch (Exception e)
            {
                log.LogError(e, "Error Getting metrics: " + e.ToString());
                throw;
            }
            
        }

        /// <summary>
        /// Gets the healthy and unhealthy node counts.
        /// </summary>
        /// <returns>The healthy and unhealthy node counts.</returns>
        /// <param name="appGw">App gw.</param>
        /// <param name="log">Log.</param>
        public static Tuple<int, int> GetHealthyAndUnhealthyNodeCounts(ApplicationGatewayBackendHealthInner appGw, ILogger log)
        {
            var healthy = 0;
            var unhealthy = 0;
            foreach (var h in appGw.BackendAddressPools[0].BackendHttpSettingsCollection[0].Servers.Select(s=>s.Health.Value))
            {
                switch (h.ToLower())
                {
                    case "healthy":
                        healthy++;
                        break;
                    default:
                        unhealthy++;
                        break;
                }
            }
            return new Tuple<int, int>(healthy, unhealthy);
        }
    }
}
