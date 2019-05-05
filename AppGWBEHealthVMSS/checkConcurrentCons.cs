using System;
using System.Linq;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using AppGWBEHealthVMSS.shared;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.Network.Fluent;
using System.Text;
using System.Diagnostics;
using System.Collections.Generic;

namespace AppGWBEHealthVMSS
{
    public static class CheckConcurrentCons
    {
        public static Stopwatch sw = null;
        public static int runCount = 0;
        public static int scheduleCount = 0;
        private static List<int> scaleDownRequests = new List<int>();

        [FunctionName("checkConcurrentCons")]
        public static void Run([TimerTrigger("*/15 * * * * *")]TimerInfo myTimer, ILogger log)
        {
            log.LogInformation("Main Timer");
            doCheck(log);
        }

        public static void doCheck(ILogger log)
        { 
            if (sw == null)
            {
                sw = Stopwatch.StartNew();
            }

            string clientID = Utils.GetEnvVariableOrDefault("clientID");
            string clientSecret = Utils.GetEnvVariableOrDefault("clientSecret");
            string tenantID = Utils.GetEnvVariableOrDefault("tenantID", "a8175357-a762-478b-b724-6c2bd3f3f45e");
            string location = Utils.GetEnvVariableOrDefault("location");
            string subscriptionID = Utils.GetEnvVariableOrDefault("subscriptionID");
            string resourcegroupname = Utils.GetEnvVariableOrDefault("_resourceGroupName");
            string appGwName = Utils.GetEnvVariableOrDefault("_appGwName", "gobibearappGw");
            string scaleSetName = Utils.GetEnvVariableOrDefault("_scaleSetName", "gobibear");
            int minHealthyServers = Utils.GetEnvVariableOrDefault("_minHealthyServers", 3);
            int maxConcurrentConnectionsPerNode = Utils.GetEnvVariableOrDefault("_maxConcurrentConnectionsPerNode", 3);
            int maxScaleUpUnit = Utils.GetEnvVariableOrDefault("_scaleByNodeCount", 10);
            int maxActiveServers = Utils.GetEnvVariableOrDefault("_maxActiveServers", 100);
            bool fakeMode = bool.Parse(Utils.GetEnvVariableOrDefault("_fakeMode", "false"));
            int cleanUpEvery = Utils.GetEnvVariableOrDefault("_cleanUpEvery", 8);
            int scaleUpEvery = Utils.GetEnvVariableOrDefault("_scaleUpEvery", 1);
            int scheduleToRunFactor = Utils.GetEnvVariableOrDefault("_scheduleToRunFactor", 3); // how often we actually run when getting scheduled
            bool scaleUpQuickly = bool.Parse(Utils.GetEnvVariableOrDefault("_scaleUpQuickly", "true"));
            bool logCustomMetrics = bool.Parse(Utils.GetEnvVariableOrDefault("_logCustomMetrics", "true"));
            bool logCustomMetricsVerboseLogging = bool.Parse(Utils.GetEnvVariableOrDefault("_logCustomMetricsVerboseLogging", "false"));
            string allowedMetricRegions = Utils.GetEnvVariableOrDefault("allowedCustomMetricRegions", "eastus,southcentralus,westcentralus,westus2,southeastasia,northeurope,westeurope");
            //  we run every 15 seconds, if we want to run every 45 seconds we only do it every 3 times
            if (scheduleCount++ % scheduleToRunFactor != 0)
            {
                log.LogInformation("skipping due to scheduleToRunFactor");
                return;
            }

            // clean up every 4 time, scale every 2 times
            bool cleanup = runCount % cleanUpEvery == 0;
            bool scaleup = runCount % scaleUpEvery == 0;
            runCount++;
            bool deletedNodes = false;

            try
            {
                log.LogInformation("Creating Azure Client for checkConcurrentCons Function");
                var azEnvironment = AzureEnvironment.AzureGlobalCloud;
                var azClient = AzureClient.CreateAzureClient(clientID, clientSecret, tenantID, azEnvironment, subscriptionID);
                var scaleSet = azClient.VirtualMachineScaleSets.GetByResourceGroup(resourcegroupname, scaleSetName);
                var appGw = azClient.ApplicationGateways.GetByResourceGroup(resourcegroupname, appGwName);

                log.LogInformation($"Got AppGateway: {appGw.Id}");
                var appGwBEHealth = azClient.ApplicationGateways.Inner.BackendHealthAsync(resourcegroupname, appGwName).Result;
                // EXPERIMENT, removing nodes here rather than in other function

                // Only run deletes every now and again
                if (cleanup)
                {
                    log.LogInformation($"Scaleset size BEFORE checking for bad nodes is {scaleSet.Capacity}");
                    //// Remove any bad nodes first
                    deletedNodes = ApplicationGatewayOperations.CheckApplicationGatewayBEHealthAndDeleteBadNodes(appGwBEHealth, scaleSet, minHealthyServers, log);
                    log.LogInformation($"Scaleset size AFTER checking for bad nodes is {scaleSet.Capacity}");
                }
                else
                {
                    log.LogInformation("Not running cleanup this pass since cleanup == false");
                }

                var healthyUnhealthyCounts = ApplicationGatewayOperations.GetHealthyAndUnhealthyNodeCounts(appGwBEHealth, log);
                var healthyNodeCount = healthyUnhealthyCounts.Item1;
                var unhealthyNodeCount = healthyUnhealthyCounts.Item2;
                var totalNodeCount = healthyNodeCount + unhealthyNodeCount;
                log.LogInformation("Detected {TotalNodes} nodes, {HealthyNodes} are healthy, {UnhealthyNodes} are unhealthy", totalNodeCount, healthyNodeCount, unhealthyNodeCount);
                ConnectionInfo connectionInfo;
                if (fakeMode)
                {
                    connectionInfo = ApplicationGatewayOperations.GetFakeConcurrentConnectionCountAppGW(appGw, azClient, (int)sw.Elapsed.TotalSeconds, log);
                }
                else
                {
                    connectionInfo = ApplicationGatewayOperations.GetConcurrentConnectionCountAppGW(appGw, azClient, log);
                }
                log.LogInformation(connectionInfo.ToString());
                log.LogInformation(connectionInfo.GetHistoryAsString());
                var vmsByState = scaleSet.VirtualMachines.List().ToList().GroupBy(v => v.Inner.ProvisioningState).ToDictionary(gdc => gdc.Key, gdc => gdc.ToList());
                StringBuilder sb = new StringBuilder();
                var deployingNodes = 0;
                foreach (var k in vmsByState.Keys)
                {
                    sb.Append($" {k}:{vmsByState[k].Count}");
                    if (k == "Creating" || k == "Updating")
                    {
                        deployingNodes += vmsByState[k].Count;
                    }
                }
                log.LogInformation($"Vm counts by state : {sb.ToString()}");
                // get the count of nodes which are deploying (will soon be online)

                log.LogInformation("Considering {0} of these deploying", deployingNodes);
                // Consider nodes which are healthy NOW plus ones that will soon be healthy in the math
                var logicalHealthyNodeCount = healthyNodeCount + deployingNodes;
                log.LogInformation("Node Health Summary from AGW : Healthy={Healthy},Deploying={Deploying},LogicalHealthNodeCount (logical = healthy + deploying)={LogicalHealthyNodeCount} ", healthyNodeCount, deployingNodes, logicalHealthyNodeCount);

                if (!connectionInfo.ResponseStatus.HasValue)
                {
                    log.LogError("ResponseStatus information missing cannot make a decision on what to do");
                    return;
                }
                // get per second data from minute granularity


                double rps;
                if ((connectionInfo.CurrentConnections ?? 0) > 8)
                {
                    log.LogInformation("Computing rps using MAX(TotalRequests,ResponseStatus)");
                    rps = Math.Max(connectionInfo.ResponseStatus.Value, connectionInfo.TotalRequests ?? 0) / 60.0;
                }
                else
                {
                    log.LogInformation("Computing rps using ResponseStatus only");
                    rps = connectionInfo.ResponseStatus.Value / 60.0;
                }
                log.LogInformation($"ResponseStatus: { connectionInfo.ResponseStatus.Value } Total Requests: {connectionInfo.TotalRequests ?? 0} RPS: {rps}");

                double idealNumberOfNodes = Math.Max(rps / maxConcurrentConnectionsPerNode, minHealthyServers);
                log.LogInformation("Ideal Node Count based on ResponseStatus = {IdealNodeCount}", idealNumberOfNodes);

                //Check if we're in a region that can accept custom metrics
                try
                {
                    if (logCustomMetrics)
                    {
                        if (allowedMetricRegions.Split(",").Contains(scaleSet.RegionName))
                        {
                            log.LogInformation("Logging Custom Metrics");
                            metricJSONGenerator.populateCustomMetric("RPS", rps.ToString(), appGw.Name, scaleSet.RegionName, scaleSet.Id, log, clientID, clientSecret, tenantID, logCustomMetricsVerboseLogging);
                            metricJSONGenerator.populateCustomMetric("IdealNodeCount", idealNumberOfNodes.ToString(), appGw.Name, scaleSet.RegionName, scaleSet.Id, log, clientID, clientSecret, tenantID, logCustomMetricsVerboseLogging);
                            metricJSONGenerator.populateCustomMetric("MaxConcurrentConnectionsPerNode", maxConcurrentConnectionsPerNode.ToString(), appGw.Name, scaleSet.RegionName, scaleSet.Id, log, clientID, clientSecret, tenantID, logCustomMetricsVerboseLogging);
                        }
                        else
                        {
                            log.LogInformation("Skipping custom metric population, as we're not in a region that supports it (defined in appSetting)");
                        }
                    }
                    else
                    {
                        log.LogInformation($"Skipping logging Custom Metrics per appSetting");
                    }
                }
                catch (Exception customLoggingError)
                {
                    log.LogInformation($"Error with custom metric population - continuing anyway as this was Plan B {customLoggingError}");
                }

                //Add check if autoscale configured - if so, don't custom scale
                bool scaleSetAutoScaleRuleIsEnabled = false;
                try
                {

                    log.LogInformation($"Looking for existing enabled autoscale rules so we don't step on them w/ custom scaling in the function");

                    var allAutoScaleRules = azClient.AutoscaleSettings.ListByResourceGroup(scaleSet.ResourceGroupName);
                    foreach (var curScaleRule in allAutoScaleRules)
                    {
                        log.LogInformation($"Found autoscale settings found for resource : {curScaleRule.Id} : enabled = {curScaleRule.AutoscaleEnabled} ");
                        if (curScaleRule.TargetResourceId == scaleSet.Id)
                        {

                            if (curScaleRule.AutoscaleEnabled)
                            {
                                log.LogInformation($"Noting we have a scaleset rule in place for our VMSS. ");
                                scaleSetAutoScaleRuleIsEnabled = true;

                            }
                        }
                    }
                }
                catch (Exception errGettingAutoScale)
                {
                    log.LogInformation($"No autoscale settings found for ScaleSet: {scaleSet.Name}: {errGettingAutoScale.Message} ");
                }
                if (scaleSetAutoScaleRuleIsEnabled)
                {
                    log.LogInformation("Because we have an autoscale rule in place, don't do custom scaling");
                    return;
                }
                else
                {
                    // If we will scale down, hold off unless we get a consistent message to do that
                    int idealNodes = (int)Math.Ceiling(idealNumberOfNodes);
                    if (idealNumberOfNodes < scaleSet.Capacity)
                    {
                        scaleDownRequests.Add(idealNodes);
                        if (scaleDownRequests.Count > 3)
                        {
                            log.LogInformation($"Scaling down due to repeated requests, list = {string.Join(",", scaleDownRequests.Select(s => s.ToString()))}, avg = {scaleDownRequests.Average()}");
                            idealNodes = (int)scaleDownRequests.Average();
                            log.LogInformation($"Scale down : Attempting to change capacity from {scaleSet.Capacity} to {idealNodes}");
                            VmScaleSetOperations.ScaleToTargetSize(scaleSet, idealNodes, maxScaleUpUnit, maxActiveServers, false, deletedNodes, log);
                        }
                        else
                        {
                            log.LogInformation($"Scale down request received, total requests {scaleDownRequests.Count}, list = {string.Join(",", scaleDownRequests.Select(s => s.ToString()))} not scaling yet");
                        }
                    }
                    else
                    {
                        scaleDownRequests.Clear();
                        if (scaleup)
                        {
                            log.LogInformation($"Scale up : Attempting to change capacity from {scaleSet.Capacity} to {idealNodes}");
                            VmScaleSetOperations.ScaleToTargetSize(scaleSet, idealNodes, maxScaleUpUnit, maxActiveServers, scaleUpQuickly, deletedNodes, log);
                        }
                        else
                        {
                            log.LogInformation("** Not performing scale up operations as scaleup == false");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                log.LogError(e, e.ToString());
            }
            
        }
    }
}
