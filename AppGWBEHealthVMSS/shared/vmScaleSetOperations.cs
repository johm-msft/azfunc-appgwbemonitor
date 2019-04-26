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
        public static void RemoveVMSSInstanceByID(IAzure azureClient,string rgName, string scaleSetName,string serverIP,ILogger log)
        {
            try
            {
                var scaleSet = azureClient.VirtualMachineScaleSets.GetByResourceGroup(rgName, scaleSetName);
                log.LogInformation("Enumerating VM Instances in ScaleSet");
                var vms = scaleSet.VirtualMachines.List();
               
                foreach (var vm in vms)
                {
                    log.LogInformation("Processing VM Instance: {0}", vm.Name);
                    var allnics = vm.ListNetworkInterfaces().ToList();
                    string nicIP = allnics[0].Inner.IpConfigurations[0].PrivateIPAddress;
                    log.LogInformation("NIC Private IP: {0}", nicIP);
                    log.LogInformation("Checking if NIC IP Matches Unhealthy Backend Address");
                    if (serverIP == nicIP)
                    {
                        log.LogInformation("Unhealthy Match Found VM: {0} with Instance ID: {1}", nicIP, vm.InstanceId);
                        var vmInstanceId = vm.InstanceId;
                        log.LogInformation("Removing VM: {0} with Instance ID: {1}", nicIP, vmInstanceId);
                        scaleSet.VirtualMachines.DeleteInstances(vmInstanceId);
                        
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
