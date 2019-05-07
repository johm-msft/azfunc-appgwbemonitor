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
using System.Threading;

namespace AppGWBEHealthVMSS.shared
{
    class VmScaleSetOperations
    {
        /// <summary>
        /// The ids of vms we have tried to delete recently
        /// </summary>
        private static Dictionary<string, DateTime> RecentPendingVMDeleteOperations = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Removes VMSS instances based on the ip addresses reported by backend
        /// IP Address.
        /// </summary>
        /// <returns>completion task.</returns>
        /// <param name="scaleSet">Scale set.</param>
        /// <param name="serverIPs">Server IPs</param>
        /// <param name="log">Log.</param>
        public static bool RemoveVMSSInstancesByIP(IVirtualMachineScaleSet scaleSet, List<string> serverIPs, ILogger log)
        {
            try
            {
                // first a bit of cleanup, remove all super old pending delete info to prevent leakage
                foreach (var k in RecentPendingVMDeleteOperations.Keys.ToList())
                {
                    if (RecentPendingVMDeleteOperations[k] < DateTime.UtcNow - TimeSpan.FromMinutes(20))
                    {
                        log.LogInformation($"Cleaning up old pending delete info for vm {k}");
                    }
                }
                log.LogInformation("Enumerating VM Instances in ScaleSet");
                var vms = scaleSet.VirtualMachines.List().ToList();
                // only consider nodes which have been prtovisioned completely for removal
                var virtualmachines = vms.Where(x => x.Inner.ProvisioningState == "Succeeded").ToList();

                log.LogInformation($"{virtualmachines.Count} machines of {vms.Count} are completely provisioned, checking those for Gobibear Intentional Panic Instances nodes");

                List<string> badInstances = new List<string>();

                foreach (var vm in virtualmachines)
                {
                    try
                    {
                        if (serverIPs.Contains(vm.ListNetworkInterfaces().First().Inner.IpConfigurations.First().PrivateIPAddress))
                        {
                            log.LogInformation("Gobibear Intentional Panic Instance detected: {0}", vm.InstanceId);
                            badInstances.Add(vm.InstanceId);
                        }
                    }
                    catch (Exception)
                    {
                        log.LogError($"Error reading ip config by VM ID {vm.Id}");
                    }
                }

                if (badInstances.Count() != 0)
                {
                    var instancesToDelete = new List<string>();
                    log.LogInformation("Removing Gobibear Intentional Panic Instances");
                    foreach (var badVm in badInstances)
                    {
                        if (RecentPendingVMDeleteOperations.ContainsKey(badVm))
                        {
                            // we have asked for this vm to be deleted before, if it's more than 10 mins ago
                            // then try again otherwise skip it
                            if (DateTime.UtcNow - RecentPendingVMDeleteOperations[badVm] < TimeSpan.FromMinutes(10))
                            {
                                log.LogInformation($"*** Instance {badVm} has recent delete request ({RecentPendingVMDeleteOperations[badVm]}), skipping deletion");
                            }
                            else
                            {
                                // we should delete it and update the timestamp
                                instancesToDelete.Add(badVm);
                            }
                        }
                        else
                        {
                            instancesToDelete.Add(badVm);
                        }
                    }
                    foreach (var v in instancesToDelete)
                    {
                        RecentPendingVMDeleteOperations[v] = DateTime.UtcNow;
                    }
                    if (instancesToDelete.Any())
                    {
                        scaleSet.VirtualMachines.DeleteInstancesAsync(instancesToDelete.ToArray());
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    log.LogInformation("No Running nodes detected to remove, likely because they are already deleting");
                    return false;
                }
            }
            catch (Exception e)
            {
                log.LogError(e, "Error Removing VMs " + e);
                throw;
            }
        }

        /// <summary>
        /// Scales the scaleset to the target size (taking into account scaling
        /// limits)
        /// </summary>
        /// <returns>The to target size.</returns>
        /// <param name="scaleSet">Scale set.</param>
        /// <param name="scaleNodeCount">Scale node count.</param>
        /// <param name="maxScaleUpCount">Max scale up count.</param>
        /// <param name="maxNodes">Max nodes.</param>
        /// <param name="scaleUpQuickly">If set to <c>true</c> scale up quickly.</param>
        /// <param name="deletedNodes">If set to <c>true</c>, we deleted nodes in this pass.</param>
        /// <param name="log">Log.</param>
        public static Task ScaleToTargetSize(IVirtualMachineScaleSet scaleSet, int scaleNodeCount, int maxScaleUpCount, int maxNodes, bool scaleUpQuickly, bool deletedNodes, ILogger log)
        {
            log.LogInformation($"ScaleToTargetSize scaleNodeCount {scaleNodeCount}, max {maxScaleUpCount}, maxNodes {maxNodes}, scaleUpQuickly {scaleUpQuickly}, deletedNodes {deletedNodes}");
            List<Task> pendingTasks = new List<Task>();
            try
            {
                if (scaleNodeCount > maxNodes)
                {
                    log.LogInformation($"Scale requested to {scaleNodeCount} which is larger than max ({maxNodes})");
                    scaleNodeCount = maxNodes;
                }
                if (scaleNodeCount > scaleSet.Capacity && scaleUpQuickly)
                {
                    // we are scaling up and want to go a fast as possible so scale by chunks
                    // of maxScaleUpCount at a time until we reach the target
                    var currentTarget = Math.Min(scaleSet.Capacity + maxScaleUpCount, scaleNodeCount);

                    do
                    {
                        log.LogInformation($"Scale quickly mode: Scaling to {currentTarget}");
                        scaleSet.Inner.Sku.Capacity = currentTarget;
                        pendingTasks.Add(scaleSet.Update().ApplyAsync());
                        if (currentTarget == scaleNodeCount)
                        {
                            break;
                        }
                        // sleep for a little bit rather than requesting again immediately
                        log.LogInformation($"Sleeping 5 seconds...");
                        Thread.Sleep(TimeSpan.FromSeconds(5));
                        currentTarget = Math.Min(currentTarget + maxScaleUpCount, scaleNodeCount);
                    } while (currentTarget <= scaleNodeCount);
                    return Task.WhenAll(pendingTasks);
                }
                else
                {
                    // If we are scaling up in slow mode and asking to scale up by more than max
                    // only scale by max nodes.
                    if (scaleNodeCount - scaleSet.Capacity > maxScaleUpCount)
                    {
                        log.LogInformation($"Scale up request too large, capacity={scaleSet.Capacity}, request = {scaleNodeCount}, scaling by {maxScaleUpCount} only");
                        scaleNodeCount = scaleSet.Capacity + maxScaleUpCount;
                    }
                    if (!deletedNodes && scaleSet.Capacity == scaleNodeCount)
                    {
                        log.LogInformation("**** Not setting scaleset size as we didn't delete any nodes this time and capacity matches");
                        return Task.CompletedTask;
                    }
                    else
                    {
                        log.LogInformation($"Changing Capacity from {scaleSet.Capacity} to {scaleNodeCount}");
                        scaleSet.Inner.Sku.Capacity = scaleNodeCount;
                        return scaleSet.Update().ApplyAsync();
                    }
                }
            }
            catch (Exception e)
            {
                log.LogInformation("Error Message: " + e.Message);
                throw;
            }
        }

        /// <summary>
        /// Disables the over provisioning for the scale set.
        /// </summary>
        /// <returns>The over provisioning.</returns>
        /// <param name="scaleSet">Scale set.</param>
        /// <param name="log">Log.</param>
        public static Task DisableOverProvisioning(IVirtualMachineScaleSet scaleSet, ILogger log)
        {
            if (scaleSet.OverProvisionEnabled)
            {
                log.LogInformation("Overprovisioning is ON, turning it off");
                scaleSet.Inner.Overprovision = false;
                return scaleSet.Update().ApplyAsync();
            }
            return Task.CompletedTask;
        }
    }
}
