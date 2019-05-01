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


namespace AppGWBEHealthVMSS.shared
{
    class VmScaleSetOperations
    {
        public static void RemoveVMSSInstanceByID(IVirtualMachineScaleSet scaleSet,List<string> serverIPs,ILogger log)
        {
            try
            {
                
                log.LogInformation("Enumerating VM Instances in ScaleSet");
                var vms = scaleSet.VirtualMachines.List();
                var virtualmachines = vms.Where(x => x.Inner.ProvisioningState == "Succeeded");
                               
                var vmssNodeCount = vms.Count();
                List<string> badInstances = new List<string>();
                
              
                foreach (var vm in virtualmachines)
                {
                   if(serverIPs.Contains(vm.ListNetworkInterfaces().First().Inner.IpConfigurations.First().PrivateIPAddress))
                   {
                         log.LogInformation("Bad Instance detected: {0}", vm.InstanceId);
                         badInstances.Add(vm.InstanceId);
                   }
                        
                     
                       

                }

                if (badInstances.Count() != 0)
                {
                    string[] badInstancesArray = badInstances.ToArray();
                    log.LogInformation("Removing Bad Instances");
                    scaleSet.VirtualMachines.DeleteInstancesAsync(badInstancesArray);
                }
                else
                {
                    log.LogInformation("No Nodes Detected to Remove");
                }
            }
            catch (Exception e)
            {
                log.LogInformation("Error Message: " + e.Message);
            }
        }
        public static void ScaleEvent(IVirtualMachineScaleSet scaleSet, int scaleNodeCount, ILogger log)
        {
            try
            {
                
                
                int scaler = scaleSet.VirtualMachines.List().Count() + scaleNodeCount;
                log.LogInformation("Scale Event in ScaleSet {0}", scaleSet.Name);
                scaleSet.Inner.Sku.Capacity = scaler;
                scaleSet.Update().ApplyAsync();



            }
            catch (Exception e)
            {
                log.LogInformation("Error Message: " + e.Message);
            }
        }
        public static void CoolDownEvent(IVirtualMachineScaleSet scaleSet, ILogger log)
        {
            try
            {
                int scaleDownCount = 10;
                int scaler = 0;
                
                
                if (scaleSet.Inner.Sku.Capacity <= 13)
                {
                    scaler = scaleSet.VirtualMachines.List().Count() - 1;
                    log.LogInformation("Current Node Capacity is less than 10 reducing scale down node count to 1");
                    
                }
                else
                {
                    log.LogInformation("Scale Down Event in ScaleSet {0}", scaleSet.Name);
                    scaler = scaleSet.VirtualMachines.List().Count() - scaleDownCount;
                    
                }

                if ((scaleSet.VirtualMachines.List().Count() - scaler) >= 3)
                {
                    log.LogInformation("VMSS Instance Count is greater than 3");
                    scaleSet.Inner.Sku.Capacity = scaler;
                    scaleSet.Update().ApplyAsync();
                }
                else
                {
                    log.LogInformation("VMSS Instance Count is less than 3, no further cooldown allowed");
                }



            }
            catch (Exception e)
            {
                log.LogInformation("Error Message: " + e.Message);
            }
        }




    }
}
