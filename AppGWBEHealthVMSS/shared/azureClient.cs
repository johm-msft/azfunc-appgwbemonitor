using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;


namespace AppGWBEHealthVMSS.shared
{
    class AzureClient
    {
        public static IAzure CreateAzureClient(string clientID, string clientSecret, string tenantID, AzureEnvironment azEnvironment, string subscriptionID)
        {
                      
            var credentials = SdkContext.AzureCredentialsFactory.FromServicePrincipal(clientID, clientSecret, tenantID, azEnvironment);
            var azureClt = Azure.Configure().WithLogLevel(HttpLoggingDelegatingHandler.Level.Basic).Authenticate(credentials).WithSubscription(subscriptionID);
            return azureClt;
        }
    }
}
