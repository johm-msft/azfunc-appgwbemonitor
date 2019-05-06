using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;


namespace AppGWBEHealthVMSS.shared
{
    /// <summary>
    /// Azure client.
    /// </summary>
    class AzureClient
    {
        /// <summary>
        /// Creates the azure client.
        /// </summary>
        /// <returns>The azure client.</returns>
        /// <param name="clientID">Client identifier.</param>
        /// <param name="clientSecret">Client secret.</param>
        /// <param name="tenantID">Tenant identifier.</param>
        /// <param name="azEnvironment">Az environment.</param>
        /// <param name="subscriptionID">Subscription identifier.</param>
        public static IAzure CreateAzureClient(string clientID, string clientSecret, string tenantID, AzureEnvironment azEnvironment, string subscriptionID)
        {
                      
            var credentials = SdkContext.AzureCredentialsFactory.FromServicePrincipal(clientID, clientSecret, tenantID, azEnvironment);
            var azureClt = Azure.Configure().WithLogLevel(HttpLoggingDelegatingHandler.Level.Basic).Authenticate(credentials).WithSubscription(subscriptionID);
            return azureClt;
        }
    }
}
