using System;
using System.Linq;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using AppGWBEHealthVMSS.shared;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.Network.Fluent;
using System.Text;

namespace AppGWBEHealthVMSS
{
    public static class CheckConcurrentCons
    {
        [FunctionName("checkConcurrentCons")]
        public static void Run([TimerTrigger("0/30 * * * * *")]TimerInfo myTimer, ILogger log)
        {
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
            int scaleByNodeCount = Utils.GetEnvVariableOrDefault("_scaleByNodeCount", 10);

            try
            {
                log.LogInformation("Creating Azure Client for checkConcurrentCons Function");
                var azEnvironment = AzureEnvironment.AzureGlobalCloud;
                var azClient = AzureClient.CreateAzureClient(clientID, clientSecret, tenantID, azEnvironment, subscriptionID);
                var scaleSet = azClient.VirtualMachineScaleSets.GetByResourceGroup(resourcegroupname, scaleSetName);
                var appGw = azClient.ApplicationGateways.GetByResourceGroup(resourcegroupname, appGwName);
                var appGwBEHealth = azClient.ApplicationGateways.Inner.BackendHealthAsync(resourcegroupname, appGwName).Result;
                var healthyUnhealthyCounts = ApplicationGatewayOperations.GetHealthyAndUnhealthyNodeCounts(appGwBEHealth, log);
                var healthyNodeCount = healthyUnhealthyCounts.Item1;
                var unhealthyNodeCount = healthyUnhealthyCounts.Item2;
                var totalNodeCount = healthyNodeCount + unhealthyNodeCount;
                log.LogInformation("Detected {TotalNodes} nodes, {HealthyNodes} are healthy, {UnhealthyNodes} are unhealthy", totalNodeCount, healthyNodeCount, unhealthyNodeCount);
                var connectionInfo = ApplicationGatewayOperations.GetConcurrentConnectionCountAppGW(appGw, azClient, log);
                log.LogInformation(connectionInfo.ToString());
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
                log.LogInformation("Node Health Summary : Healthy={Healthy},Deploying={Deploying},LogicalHealthNodeCount={LogicalHealthyNodeCount} ", healthyNodeCount, deployingNodes, logicalHealthyNodeCount);

                if (!connectionInfo.ResponseStatus.HasValue)
                {
                    log.LogError("ResponseStatus information missing cannot make a decision on what to do");
                    return;
                }
                // get per second data from minute granularity
                var rps = connectionInfo.ResponseStatus.Value / 60;
                //var avgRequestsPerSecondPerNode = rps / logicalHealthyNodeCount;

                var idealNumberOfNodes = Math.Max(rps / maxConcurrentConnectionsPerNode, minHealthyServers);
                log.LogInformation("Ideal Node Count based on ResponseStatus = {IdealNodeCount}", idealNumberOfNodes);

                //var avgConnectionsPerNode = Math.Ceiling((double)connectionInfo.CurrentConnections / logicalHealthyNodeCount);
                //log.LogInformation("Average Connections Per Node is {AvgConnectionsPerNode}", avgConnectionsPerNode);

                //if (!connectionInfo.CurrentConnections.HasValue)
                //{
                //    log.LogError("Connection information missing cannot make a decision on what to do");
                //    return;
                //}

                //var idealNumberOfNodes = Math.Max(connectionInfo.CurrentConnections.Value / maxConcurrentConnectionsPerNode, minHealthyServers);
                //log.LogInformation("Ideal Node Count based on CurrentConnections as {IdealNodeCount}", idealNumberOfNodes);

                //if (connectionInfo.TotalRequests.HasValue)
                //{
                //    // get per second data from minute granularity
                //    var rps = connectionInfo.TotalRequests.Value / 60;
                //    var i = Math.Max(rps / maxConcurrentConnectionsPerNode, minHealthyServers);
                //    log.LogInformation("Ideal Node Count based on TotalRequests = {IdealNodeCount}", i);
                //}
                //if (connectionInfo.ResponseStatus.HasValue)
                //{
                //    // get per second data from minute granularity
                //    var rps = connectionInfo.ResponseStatus.Value / 60;
                //    var i = Math.Max(rps / maxConcurrentConnectionsPerNode, minHealthyServers);
                //    log.LogInformation("Ideal Node Count based on ResponseStatus = {IdealNodeCount}", i);
                //}

               
                // we need to deploy new nodes to bring the healthy node count up close to the desired count
                var newNodes = idealNumberOfNodes - logicalHealthyNodeCount;
                log.LogInformation("Calculated new nodes needed = {NewNodeCount}", newNodes);
                if (newNodes == 0)
                {
                    log.LogInformation("No new nodes needed");
                }
                // if we are over the threshold then scale up
                else if (newNodes > 0)
                {
                    // scale up by either the newnode count or the default
                    // because we only want to scale by the max number at a time
                    var scaleNodeCount = Math.Min(newNodes, scaleByNodeCount);
                    log.LogInformation("Scale Event Initiated scaling up by {0} nodes", scaleNodeCount);
                    VmScaleSetOperations.ScaleEvent(scaleSet, scaleNodeCount, log);
                }
                else if (healthyNodeCount >= idealNumberOfNodes)
                {
                    log.LogInformation("Scaling Down");
                    // Scale down slower than scale up (half the rate)
                    VmScaleSetOperations.CoolDownEvent(scaleSet, scaleByNodeCount / 2, minHealthyServers, log);
                }
                else
                {
                    log.LogInformation("Sclaing skipped due to healthy node count");
                }

                //if (healthyNodeCount == 0)
                //{
                //    if (deployingNodes >= minHealthyServers)
                //    {
                //        log.LogInformation("No healthy nodes found, {deployingNodesCount} nodes are already deploying, skipping scale up", deployingNodes);
                //    }
                //    else
                //    {
                //        log.LogInformation("No healthy nodes found, scaling up");
                //        VmScaleSetOperations.ScaleEvent(scaleSet, minHealthyServers, log);
                //    }
                //}
                //else
                //{

                //}
            }
            catch (Exception e)
            {
                log.LogError(e, e.ToString());
            }
            
        }
    }
}
