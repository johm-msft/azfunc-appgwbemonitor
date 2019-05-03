using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using Microsoft.Azure.Management.Compute;
using Microsoft.Azure.Management.Compute.Fluent;
using Microsoft.Azure.Management.Network;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.Network.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace AppGWBEHealthVMSS.shared
{
    class VmScaleSetOperations
    {
        public static Task RemoveVMSSInstanceByID(IVirtualMachineScaleSet scaleSet, List<string> serverIPs, ILogger log)
        {
            try
            {
                // TODO: would be nice to pass a flag to this function saying only delete unhealthy nodes that
                // have been up for > 30 seconds or something to allow them to start and be recognized
                log.LogInformation("Enumerating VM Instances in ScaleSet");
                var vms = scaleSet.VirtualMachines.List().ToList();
                // only consider nodes which have been prtovisioned completely for removal
                var virtualmachines = vms.Where(x => x.Inner.ProvisioningState == "Succeeded").ToList();

                log.LogInformation($"{virtualmachines.Count} machines of {vms.Count} are completely provisioned, checking those for unhealthy nodes");

                List<string> badInstances = new List<string>();
              
                foreach (var vm in virtualmachines)
                {
                    try
                    {
                        if (serverIPs.Contains(vm.ListNetworkInterfaces().First().Inner.IpConfigurations.First().PrivateIPAddress))
                        {
                            log.LogInformation("Bad Instance detected: {0}", vm.InstanceId);
                            badInstances.Add(vm.InstanceId);
                        }
                    }
                    catch(Exception)
                    {
                        log.LogError($"Error reading ip config by vm id {vm.Id}");
                    }
                }

                if (badInstances.Count() != 0)
                {
                    string[] badInstancesArray = badInstances.ToArray();
                    log.LogInformation("Removing Bad Instances");
                    return scaleSet.VirtualMachines.DeleteInstancesAsync(badInstancesArray);
                }
                else
                {
                    log.LogInformation("No Running nodes detected to remove, likely because they are already deleting");
                    return Task.CompletedTask;
                }
            }
            catch (Exception e)
            {
                log.LogError(e, "Error Removing VMs " + e);
                throw;
            }
        }

        public static Task ScaleEvent(IVirtualMachineScaleSet scaleSet, int scaleNodeCount, ILogger log)
        {
            try
            {
                var vms = scaleSet.VirtualMachines.List().ToList();
                int scaler = vms.Count() + scaleNodeCount;
                var maxNodes = 100;// hard code to 100 TODO: make configurable
                //var maxNodes = scaleSet.Inner.SinglePlacementGroup ?? true ? 100 : 1000;

                if (scaler > maxNodes)
                {
                    log.LogInformation("Scale request for ScaleSet {Scaleset} to {RequestedScale} nodes exceeeds limit, scaling to max allowed({MaxScale})",
                        scaleSet.Name, scaler, maxNodes);
                    scaler = maxNodes;
                }

                log.LogInformation("Scale Event in ScaleSet {0} to {1} nodes", scaleSet.Name, scaler);
                scaleSet.Inner.Sku.Capacity = scaler;
                return scaleSet.Update().ApplyAsync();
            }
            catch (Exception e)
            {
                log.LogInformation("Error Message: " + e.Message);
                throw;
            }
        }

        /// <summary>
        /// Scale down the pool by a hueristic amount of nodes
        /// </summary>
        /// <param name="scaleSet">Scale set.</param>
        /// <param name="maxNumberOfNodesToScaleBy">Max number of nodes to scale by.</param>
        /// <param name="minHealthyNodes">Minimum healthy nodes.</param>
        /// <param name="log">Log.</param>
        public static void CoolDownEvent(IVirtualMachineScaleSet scaleSet, int maxNumberOfNodesToScaleBy, int minHealthyNodes, ILogger log)
        {
            try
            {
                // don't be super agressive, assume min bound is base + scale factor
                int baseSteadyStateCount = maxNumberOfNodesToScaleBy + minHealthyNodes;

                var targetNodeCount = minHealthyNodes;

                var currentVmCount = (int)scaleSet.Inner.Sku.Capacity;

                // if we are below min + scale factor then go down by 1 at a time
                if (currentVmCount <= baseSteadyStateCount)
                {
                    // just scale down by one node
                    targetNodeCount = currentVmCount - 1;
                }
                else
                {
                    targetNodeCount = Math.Max(currentVmCount - maxNumberOfNodesToScaleBy, baseSteadyStateCount);
                }

                if (targetNodeCount < minHealthyNodes)
                {
                    targetNodeCount = minHealthyNodes;
                }
                if (scaleSet.Inner.Sku.Capacity > targetNodeCount)
                {
                    log.LogInformation("Scale Down Event in ScaleSet {0} Scaling down to {1} nodes ", scaleSet.Name, targetNodeCount);
                    scaleSet.Inner.Sku.Capacity = targetNodeCount;
                    scaleSet.Update().ApplyAsync();
                }
                else
                {
                    log.LogInformation("No need to scale down, already at target count ({count})", targetNodeCount);
                }
            }
            catch (Exception e)
            {
                log.LogInformation("Error Message: " + e.Message);
            }
        }




    }
}
