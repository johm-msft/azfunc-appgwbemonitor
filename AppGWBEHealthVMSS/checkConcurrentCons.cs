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
        private static List<int> scaleDownRequests = new List<int>();

        static bool doRemainder = false;
        [FunctionName("checkConcurrentCons")]
        public static void Run([TimerTrigger("0 * * * * *")]TimerInfo myTimer, ILogger log)
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
            // do full run every 3 times
            bool cleanup = runCount % 3 == 0;
            bool scaleup = runCount % 2 == 0;
            runCount++;

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
            bool fakeMode = bool.Parse(Utils.GetEnvVariableOrDefault("_fakeMode", "false"));

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
                    ApplicationGatewayOperations.CheckApplicationGatewayBEHealthAndDeleteBadNodes(appGwBEHealth, scaleSet, minHealthyServers, log);
                    log.LogInformation($"Scaleset size AFTER checking for bad nodes is {scaleSet.Capacity}");
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
                //TODO: Add logic if ResponseStatus and TotalRequests are out of sync just ignore it for now 'cause AppGW has crazy metrics

                //
                double rps = Math.Max(connectionInfo.ResponseStatus.Value, connectionInfo.TotalRequests ?? 0) / 60.0;
                log.LogInformation($"ResponseStatus: { connectionInfo.ResponseStatus.Value } Total Requests: {connectionInfo.TotalRequests ?? 0} RPS: {rps}");

                double idealNumberOfNodes = Math.Max(rps / maxConcurrentConnectionsPerNode, minHealthyServers);
                log.LogInformation("Ideal Node Count based on ResponseStatus = {IdealNodeCount}", idealNumberOfNodes);

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
                        VmScaleSetOperations.ScaleToTargetSize(scaleSet, idealNodes, maxScaleUpUnit, log);
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
                        VmScaleSetOperations.ScaleToTargetSize(scaleSet, idealNodes, maxScaleUpUnit, log);
                    }
                    else
                    {
                        log.LogInformation("** Not performing scale up operations as scaleup == false");
                    }
                }
            }
            catch (Exception e)
            {
                doRemainder = false;
                log.LogError(e, e.ToString());
            }
            
        }
    }
}
