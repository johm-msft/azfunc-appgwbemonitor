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
            string resourcegroupname = Utils.GetEnvVariableOrDefault("resourceGroupName");
            string appGwName = Utils.GetEnvVariableOrDefault("appGwName", "gobibearappGw");
            string scaleSetName = Utils.GetEnvVariableOrDefault("scaleSetName", "gobibear");
            int minHealthyServers = Utils.GetEnvVariableOrDefault("minHealthyServers", 3);
            int maxConcurrentConnectionsPerNode = Utils.GetEnvVariableOrDefault("maxConcurrentConnectionsPerNode", 3);
            int scaleByNodeCount = Utils.GetEnvVariableOrDefault("scaleByNodeCount", 10);

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
                var connectionInfo = ApplicationGatewayOperations.GetConcurrentConnectionCountAppGW(appGw, azClient,log);
                log.LogInformation("Current Connection Count is {ConnectionCount}, TotalRequests = {TotalRequests}", connectionInfo.CurrentConnections, connectionInfo.TotalRequests);
                var vmsByState = scaleSet.VirtualMachines.List().ToList().GroupBy(v =>v.Inner.ProvisioningState).ToDictionary(gdc => gdc.Key, gdc => gdc.ToList());
                StringBuilder sb = new StringBuilder();
                var deployingNodes = 0;
                foreach(var k in vmsByState.Keys)
                {
                    sb.Append($" {k}:{vmsByState[k].Count}");
                    if (k == "Creating" || k == "Updating")
                    {
                        deployingNodes+=vmsByState[k].Count;
                    }
                }
                log.LogInformation($"Vm counts by state : {sb.ToString()}");
                // get the count of nodes which are deploying (will soon be online)

                log.LogInformation("Considering {0} of these deploying", deployingNodes);
                // Consider nodes which are healthy NOW plus ones that will soon be healthy in the math
                var logicalHealthyNodeCount = healthyNodeCount + deployingNodes;
                log.LogInformation("Node Health Summary : Healthy={Healthy},Deploying={Deploying},LogicalHealthNodeCount={LogicalHealthyNodeCount} ", healthyNodeCount, deployingNodes, logicalHealthyNodeCount);
                if (healthyNodeCount == 0)
                {
                    if (deployingNodes >= minHealthyServers)
                    {
                        log.LogInformation("No healthy nodes found, {deployingNodesCount} nodes are already deploying, skipping scale up", deployingNodes);
                    }
                    else
                    {
                        log.LogInformation("No healthy nodes found, scaling up");
                        VmScaleSetOperations.ScaleEvent(scaleSet, minHealthyServers, log);
                    }
                }
                else
                {
                    if (connectionInfo.CurrentConnections > 0)
                    {

                        var avgConnectionsPerNode = Math.Ceiling((double)connectionInfo.CurrentConnections / logicalHealthyNodeCount);
                        log.LogInformation("Average Connections Per Node is {AvgConnectionsPerNode}", avgConnectionsPerNode);
                        var idealNumberOfNodes = Math.Max(connectionInfo.CurrentConnections / maxConcurrentConnectionsPerNode, minHealthyServers);
                        log.LogInformation("Calculated Ideal Node Count as {IdealNodeCount}", idealNumberOfNodes);
                        // we need to deploy new nodes to bring the healthy node count up close to the desired count
                        var newNodes = idealNumberOfNodes - logicalHealthyNodeCount;
                        log.LogInformation("Calculated new nodes needed = {NewNodeCount}", newNodes);

                        // if we are over the threshold then scale up
                        if (avgConnectionsPerNode > maxConcurrentConnectionsPerNode)
                        {
                            // scale up by either the newnode count or the default
                            // because we only want to scale by the max number at a time
                            var scaleNodeCount = Math.Min(newNodes, scaleByNodeCount);
                            log.LogInformation("Scale Event Initiated scaling up by {0} nodes", scaleNodeCount);
                            VmScaleSetOperations.ScaleEvent(scaleSet, scaleNodeCount, log);
                        }
                        else if (avgConnectionsPerNode < maxConcurrentConnectionsPerNode - 1)
                        {
                            log.LogInformation("Scale Down");
                            VmScaleSetOperations.CoolDownEvent(scaleSet, scaleByNodeCount, minHealthyServers, log);
                        }
                    }
                    else
                    {
                        log.LogInformation("Concurrent Count is {ConnectionCount}, no action required", connectionInfo.CurrentConnections);
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
