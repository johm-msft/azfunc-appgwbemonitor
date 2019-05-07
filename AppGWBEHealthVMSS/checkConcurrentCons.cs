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
    /// <summary>
    /// Class containing function to run hosted in AzureFunctions to monitor the
    /// resource group and perform scale operations as needed (delete nodes, 
    /// scale up, scale down etc).
    /// </summary>
    public static class CheckConcurrentCons
    {
        public static Stopwatch sw = null;
        public static int runCount = 0;
        public static int scheduleCount = 0;
        private static bool checkedOverProvisioningStatus = false;
        private static List<int> scaleDownRequests = new List<int>();

        /// <summary>
        /// The function which fires on a timer to run the scale operations.
        /// </summary>
        /// <param name="myTimer">My timer.</param>
        /// <param name="log">Log.</param>
        [FunctionName("checkConcurrentCons")]
        public static void Run([TimerTrigger("*/15 * * * * *")]TimerInfo myTimer, ILogger log)
        {
            log.LogInformation("Main Timer");
            DoCheck(log);
        }

        /// <summary>
        /// Performs the checks and scale/delete operations.
        /// </summary>
        /// <param name="log">Log.</param>
        public static void DoCheck(ILogger log)
        { 
            // on first run we start a stopwatch to track how long into it we
            // are (this is only used for fake load mode which is for testing)
            if (sw == null)
            {
                sw = Stopwatch.StartNew();
            }
            // Read all the settings from the environment with sensible defaults
            // where applicable
            string clientID = Utils.GetEnvVariableOrDefault("clientID");
            string clientSecret = Utils.GetEnvVariableOrDefault("clientSecret");
            string tenantID = Utils.GetEnvVariableOrDefault("tenantID", "a8175357-a762-478b-b724-6c2bd3f3f45e");
            string location = Utils.GetEnvVariableOrDefault("location");
            string subscriptionID = Utils.GetEnvVariableOrDefault("subscriptionID");
            string resourcegroupname = Utils.GetEnvVariableOrDefault("resourceGroupName");
            string appGwName = Utils.GetEnvVariableOrDefault("appGwName", "gobibearappGw");
            string scaleSetName = Utils.GetEnvVariableOrDefault("_scaleSetName", "gobibear");
            int minHealthyServers = Utils.GetEnvVariableOrDefault("_minHealthyServers", 3);
            int healthBuffer = Utils.GetEnvVariableOrDefault("healthBuffer", 3);
            int maxConcurrentConnectionsPerNode = Utils.GetEnvVariableOrDefault("_maxConcurrentConnectionsPerNode", 3);
            int maxScaleUpUnit = Utils.GetEnvVariableOrDefault("_scaleByNodeCount", 10);
            int maxActiveServers = Utils.GetEnvVariableOrDefault("_maxActiveServers", 100);
            bool fakeMode = bool.Parse(Utils.GetEnvVariableOrDefault("fakeMode", "false"));
            int cleanUpEvery = Utils.GetEnvVariableOrDefault("cleanUpEvery", 4);
            int scaleUpEvery = Utils.GetEnvVariableOrDefault("scaleUpEvery", 1);
            int scheduleToRunFactor = Utils.GetEnvVariableOrDefault("scheduleToRunFactor", 3); // how often we actually run when getting scheduled
            bool scaleUpQuickly = bool.Parse(Utils.GetEnvVariableOrDefault("scaleUpQuickly", "true"));
            bool logCustomMetrics = bool.Parse(Utils.GetEnvVariableOrDefault("logCustomMetrics", "false"));
            bool logCustomMetricsVerboseLogging = bool.Parse(Utils.GetEnvVariableOrDefault("logCustomMetricsVerboseLogging", "false"));

            // To get around CRON syntax limitations we can't actually run every 45 seconds
            // instead we get scheduled every 15 seconds and only actually run
            // every {scheduleToRunFactor} times.
            if (scheduleCount++ % scheduleToRunFactor != 0)
            {
                log.LogInformation("skipping due to scheduleToRunFactor");
                return;
            }

            // clean up every {cleanUpEvery} times, scale every {scaleUpEvery} times
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


                // We want to make sure that overprovisioning if OFF on the scaleset
                // since we are creating and deleting vms very often it makes sense to 
                // get the exact counts.
                // Avoid extra api calls by checking this only once and setting flag once we have checked.
                if (!checkedOverProvisioningStatus)
                {
                    VmScaleSetOperations.DisableOverProvisioning(scaleSet, log);
                    checkedOverProvisioningStatus = true;
                }

                log.LogInformation($"Got AppGateway: {appGw.Id}");
                var appGwBEHealth = azClient.ApplicationGateways.Inner.BackendHealthAsync(resourcegroupname, appGwName).Result;

                // Only run deletes every now and again
                
                if (cleanup)
                {
                    log.LogInformation($"Scaleset size BEFORE checking for Gobibear Intentional Panic Instance nodes is {scaleSet.Capacity}");
                    //// Remove any bad nodes first
                    deletedNodes = ApplicationGatewayOperations.CheckApplicationGatewayBEHealthAndDeleteBadNodes(appGwBEHealth, scaleSet, minHealthyServers, log);
                    log.LogInformation($"Scaleset size AFTER checking for Gobibear Intentional Panic Instance nodes is {scaleSet.Capacity}");
                }
                else
                {
                    log.LogInformation("Not running cleanup this pass since cleanup == false");
                }
                
                var healthyUnhealthyCounts = ApplicationGatewayOperations.GetHealthyAndUnhealthyNodeCounts(appGwBEHealth, log);
                var healthyNodeCount = healthyUnhealthyCounts.Item1;
                var unhealthyNodeCount = healthyUnhealthyCounts.Item2;
                var totalNodeCount = healthyNodeCount + unhealthyNodeCount;
                log.LogInformation("Detected {TotalNodes} nodes, {HealthyNodes} are healthy, {UnhealthyNodes} are unhealthy (creating & deleting)", totalNodeCount, healthyNodeCount, unhealthyNodeCount);
                ConnectionInfo connectionInfo;
                if (fakeMode)
                {
                    connectionInfo = ApplicationGatewayOperations.GetFakeConnectionMetrics(appGw, azClient, (int)sw.Elapsed.TotalSeconds, log);
                }
                else
                {
                    connectionInfo = ApplicationGatewayOperations.GetConnectionMetrics(appGw, azClient, log);
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
                log.LogInformation($"VM counts by state : {sb.ToString()}");
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

                double rps;

                log.LogInformation("Computing rps using ResponseStatus only");
                rps = connectionInfo.ResponseStatus.Value / 60.0;
                log.LogInformation($"ResponseStatus: { connectionInfo.ResponseStatus.Value } Total Requests: {connectionInfo.TotalRequests ?? 0} RPS: {rps}");

                double idealNumberOfNodes = Math.Max(rps / maxConcurrentConnectionsPerNode, minHealthyServers);
                log.LogInformation("Ideal Node Count based on ResponseStatus = {IdealNodeCount}", idealNumberOfNodes);
                double rpsPerNode = rps / Math.Max(healthyNodeCount, 1);

                // Sanity check that the number makes sense
                if (idealNumberOfNodes > 160)
                {
                    log.LogInformation("Ideal Node Count looks incorrect, possibly due to transient metrics ({IdealNodeCount}), ignoring scale event", idealNumberOfNodes);
                }
                else
                {
                    if (logCustomMetrics)
                    {
                        log.LogInformation("Logging Custom Metrics");
                        metricJSONGenerator.populateCustomMetric("RPSPerInstance", rpsPerNode.ToString(), appGw.Name, scaleSet.RegionName, scaleSet.Id, log, clientID, clientSecret, tenantID, logCustomMetricsVerboseLogging);
                        //metricJSONGenerator.populateCustomMetric("RPS", rps.ToString(), appGw.Name, scaleSet.RegionName, scaleSet.Id, log, clientID, clientSecret, tenantID, logCustomMetricsVerboseLogging);
                        //metricJSONGenerator.populateCustomMetric("IdealNodeCount", idealNumberOfNodes.ToString(), appGw.Name, scaleSet.RegionName, scaleSet.Id, log, clientID, clientSecret, tenantID, logCustomMetricsVerboseLogging);
                        //metricJSONGenerator.populateCustomMetric("MaxConcurrentConnectionsPerNode", maxConcurrentConnectionsPerNode.ToString(), appGw.Name, scaleSet.RegionName, scaleSet.Id, log, clientID, clientSecret, tenantID, logCustomMetricsVerboseLogging);
                        return;
                    }
                    else
                    {
                        log.LogInformation($"Skipping logging Custom Metrics per appSetting - not used in production");
                    }
                    // If we will scale down, hold off unless we get a consistent message to do that
                    int idealNodes = (int)Math.Ceiling(idealNumberOfNodes);

                    idealNodes += healthBuffer;
                    if (idealNodes < scaleSet.Capacity)
                    {
                        scaleDownRequests.Add(idealNodes);
                        if (scaleDownRequests.Count > 3)
                        {
                            log.LogInformation($"Scaling down due to repeated requests, list = {string.Join(",", scaleDownRequests.Select(s => s.ToString()))}, avg = {scaleDownRequests.Average()}");
                            idealNodes = (int)scaleDownRequests.Average();
                            log.LogInformation($"Scale down : Attempting to change capacity from {scaleSet.Capacity} to {idealNodes}");
                            VmScaleSetOperations.ScaleToTargetSize(scaleSet, idealNodes, maxScaleUpUnit, maxActiveServers, false, deletedNodes, log);
                            // wipe out the list
                            scaleDownRequests.Clear();
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
                            log.LogInformation("** Not performing scale up operations as scaleup == false (45 second CRON adjustment)");
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
